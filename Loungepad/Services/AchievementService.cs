using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Loungepad.Models;

namespace Loungepad.Services;

/// <summary>
/// Keeps every game's achievements current and says when a session unlocked one.
///
/// Three moments ask for a fetch. A game's page opening with a list older than a few hours;
/// the background pass after each scan, which walks the library newest-played first and stops
/// the moment a source refuses (a rate limit, a dead network) rather than failing once per game;
/// and the end of a session, a few seconds after the game closes, because that is when the
/// list has changed -- the answer is compared with the one from before the session, and the
/// difference is what the page announces. A first fetch has nothing to compare against and
/// announces nothing, or every achievement earned years ago would arrive as news.
///
/// Every fetch goes to one provider, chosen by what the game is (see IAchievementProvider), and
/// a fetch that fails keeps the last good list with the reason beside it. The icons are cached
/// on disk after the fact, one at a time, so the list is never held up by a hundred small
/// downloads and a list opened offline still has its pictures.
/// </summary>
public sealed class AchievementService : IDisposable
{
    /// <summary>A list older than this is fetched again when its page opens.</summary>
    public static readonly TimeSpan Fresh = TimeSpan.FromHours(6);
    /// <summary>The background pass leaves a list newer than this alone...</summary>
    public static readonly TimeSpan PassFresh = TimeSpan.FromHours(24);
    /// <summary>...and one for a game never played here and not installed, for much longer: its
    /// unlocks only change on another PC, and a library of hundreds costs a call each.</summary>
    public static readonly TimeSpan IdleFresh = TimeSpan.FromDays(7);
    /// <summary>The most games one pass asks about.</summary>
    public const int PassCap = 400;
    /// <summary>Between games in the pass: Steam's Web API and the shared service both have a
    /// budget, and nothing about a background list is urgent.</summary>
    private static readonly TimeSpan PassGap = TimeSpan.FromMilliseconds(1200);

    private readonly AchievementStore _store;
    private readonly LibraryStore _library;
    private readonly Func<AppSettings> _settings;
    private readonly IReadOnlyList<IAchievementProvider> _providers;
    private readonly GameLaunchService _launcher;
    private readonly object _gate = new();
    private readonly Dictionary<string, Task<GameAchievements?>> _inflight = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _passGate = new(1, 1);
    private readonly SemaphoreSlim _iconGate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();

    /// <summary>A fetch finished, with a new list or a fresh error on the old one. Off the UI thread.</summary>
    public event Action<GameAchievements>? Fetched;
    /// <summary>A session unlocked these. Off the UI thread, with the icons already cached.</summary>
    public event Action<Game, List<Achievement>>? Unlocked;
    /// <summary>More of a game's icons are on disk now. Off the UI thread.</summary>
    public event Action<string>? IconsCached;

    public AchievementService(AchievementStore store, LibraryStore library, Func<AppSettings> settings,
        IEnumerable<IAchievementProvider> providers, GameLaunchService launcher)
    {
        _store = store;
        _library = library;
        _settings = settings;
        _providers = providers.ToList();
        _launcher = launcher;
        launcher.SessionEnded += OnSessionEnded;
    }

    public AchievementStore Store => _store;
    public IReadOnlyList<IAchievementProvider> Providers => _providers;
    public bool Enabled => _settings().AchievementsEnabled;

    public IAchievementProvider? ProviderFor(Game g) => _providers.FirstOrDefault(p => p.Supports(g));
    /// <summary>Why a fetch would not happen right now, or null.</summary>
    public string? Blocked(Game g) => ProviderFor(g)?.Blocked(g);
    public GameAchievements? Get(string gameId) => _store.Get(gameId);
    public Dictionary<string, AchievementSummary> Summaries() => _store.Summaries();

    /// <summary>Whether the list on record is worth showing without asking again first.</summary>
    public static bool IsFresh(GameAchievements? set) =>
        set is not null && set.FetchedAt != default && DateTime.UtcNow - set.FetchedAt < Fresh;

    /// <summary>
    /// Fetches the game's list unless one fresh enough is on record. One fetch per game at a
    /// time: a page opened twice while the first ask is in flight waits on it rather than asking
    /// again. Null when nothing can be asked (no provider, or achievements are off).
    /// </summary>
    public Task<GameAchievements?> RefreshAsync(Game game, bool force, CancellationToken ct = default)
    {
        if (!Enabled) return Task.FromResult<GameAchievements?>(null);
        var provider = ProviderFor(game);
        if (provider is null) return Task.FromResult<GameAchievements?>(null);
        var previous = _store.Get(game.Id);
        if (!force && IsFresh(previous) && previous!.Error is null) return Task.FromResult<GameAchievements?>(previous);

        lock (_gate)
        {
            if (_inflight.TryGetValue(game.Id, out var running)) return running;
            var task = FetchAsync(game, provider, previous, ct);
            _inflight[game.Id] = task;
            return task;
        }
    }

    private async Task<GameAchievements?> FetchAsync(Game game, IAchievementProvider provider, GameAchievements? previous, CancellationToken ct)
    {
        try
        {
            if (provider.Blocked(game) is { } why)
            {
                // Not an error to keep on disk: it is about the account, not the game, and it goes
                // away the moment the sign-in or key is added. Reported once for the page.
                var blocked = AchievementJson.Failed(game, provider.Source, previous, why);
                Fetched?.Invoke(blocked);
                return blocked;
            }
            var set = await provider.FetchAsync(game, previous, ct);
            if (set.Error is null || previous is not null || set.Items.Count > 0) _store.Put(set);
            if (set.Error is null)
                Log.Info($"Achievements: {game.Title} via {provider.Source}: {set.Unlocked}/{set.Total}" + (set.SourceName is { } n && n != game.Title ? $" ({n})" : ""));
            else
                Log.Info($"Achievements: {game.Title} via {provider.Source}: {set.Error}");
            try { Fetched?.Invoke(set); } catch (Exception ex) { Log.Info($"Achievements: handler failed ({ex.Message})"); }
            return set;
        }
        catch (Exception ex)
        {
            Log.Info($"Achievements: {game.Title} failed: {ex.Message}");
            return previous;
        }
        finally
        {
            lock (_gate) _inflight.Remove(game.Id);
        }
    }

    /// <summary>
    /// The background pass: every supported game, newest-played first, skipping what is fresh
    /// enough, paced, and stopping for a source that has refused. One pass at a time; a second
    /// ask while one runs is dropped, since the running one will get there.
    /// </summary>
    public async Task RefreshAllAsync(bool force)
    {
        if (!Enabled) return;
        if (!await _passGate.WaitAsync(0)) return;
        try
        {
            foreach (var p in _providers) p.ResetPass();
            var games = _library.Games.ToList()
                .Where(g => !g.Hidden && ProviderFor(g) is not null)
                .OrderByDescending(g => g.Installed || g.Sessions > 0)
                .ThenByDescending(g => g.LastPlayed ?? DateTime.MinValue)
                .Take(PassCap)
                .ToList();
            var asked = 0;
            foreach (var g in games)
            {
                if (_cts.IsCancellationRequested) return;
                var provider = ProviderFor(g)!;
                if (provider.Unavailable || provider.Blocked(g) is not null) continue;
                var have = _store.Get(g.Id);
                if (!force && have is not null && have.FetchedAt != default)
                {
                    var window = g.Installed || g.Sessions > 0 ? PassFresh : IdleFresh;
                    if (DateTime.UtcNow - have.FetchedAt < window) continue;
                }
                await RefreshAsync(g, force: true, _cts.Token);
                asked++;
                try { await Task.Delay(PassGap, _cts.Token); } catch (TaskCanceledException) { return; }
            }
            if (asked > 0) Log.Info($"Achievements: pass asked about {asked} game(s)");
        }
        catch (Exception ex) { Log.Info($"Achievements: pass failed: {ex.Message}"); }
        finally { _passGate.Release(); }
    }

    /// <summary>After a session that counted: fetch again, compare, announce.</summary>
    private void OnSessionEnded(Game game, DateTime start, DateTime end, bool counted)
    {
        if (!counted || !Enabled || ProviderFor(game) is null) return;
        _ = Task.Run(async () =>
        {
            try
            {
                // The store needs a moment to hear from the client that just closed.
                await Task.Delay(TimeSpan.FromSeconds(5), _cts.Token);
                var before = _store.Get(game.Id);
                var after = await RefreshAsync(game, force: true, _cts.Token);
                if (after is null || after.Error is not null) return;
                var fresh = AchievementStore.NewlyUnlocked(before, after);
                if (fresh.Count == 0) return;
                Log.Info($"Achievements: {game.Title} unlocked {fresh.Count} this session");
                if (!_settings().AchievementNotifications) return;
                await CacheIconsAsync(after, fresh, _cts.Token);
                Unlocked?.Invoke(game, fresh);
            }
            catch (TaskCanceledException) { }
            catch (Exception ex) { Log.Info($"Achievements: post-session check failed: {ex.Message}"); }
        });
    }

    // ---- icons ----

    /// <summary>The file an icon URL would be cached as, or null when it is not on disk.</summary>
    public static string? IconFileIfCached(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        var name = IconName(url);
        return File.Exists(Path.Combine(Paths.AchievementIconsDir, name)) ? name : null;
    }

    /// <summary>
    /// An icon URL as it may be handed to the page or fetched, or null. Icon URLs arrive from the
    /// stores' APIs and the metadata service and are written into the page's markup, so only an
    /// absolute https URL made of ordinary URL characters passes -- no quotes, brackets, spaces or
    /// control characters, nothing that could end an attribute, and no scheme but https.
    /// </summary>
    public static string? SafeIconUrl(string? url)
    {
        if (string.IsNullOrEmpty(url) || url.Length > 2048) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) return null;
        foreach (var c in url)
            if (c <= ' ' || c >= 0x7f || c is '"' or '\'' or '<' or '>' or '\\' or '`' or '(' or ')') return null;
        return url;
    }

    public static string IconName(string url)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url)))[..20].ToLowerInvariant();
        var path = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.AbsolutePath : url;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".jpg" or ".jpeg" or ".png" or ".webp" or ".gif")) ext = ".img";
        return hash + ext;
    }

    /// <summary>Downloads whichever of the set's icons are not on disk yet, one at a time. Only
    /// the given items when there are some (a notification's), else all of them.</summary>
    public async Task CacheIconsAsync(GameAchievements set, IEnumerable<Achievement>? only = null, CancellationToken ct = default)
    {
        var urls = (only ?? set.Items).SelectMany(a => new[] { a.IconUrl, a.IconLockedUrl })
            .Select(SafeIconUrl).Where(u => u is not null).Select(u => u!).Distinct().ToList();
        var missing = urls.Where(u => !File.Exists(Path.Combine(Paths.AchievementIconsDir, IconName(u)))).ToList();
        if (missing.Count == 0) return;
        await _iconGate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Paths.AchievementIconsDir);
            var got = 0;
            foreach (var url in missing)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    using var res = await AchievementJson.Http.GetAsync(url, ct);
                    if (!res.IsSuccessStatusCode) continue;
                    var bytes = await res.Content.ReadAsByteArrayAsync(ct);
                    if (bytes.Length == 0 || bytes.Length > 2 * 1024 * 1024) continue;
                    var dest = Path.Combine(Paths.AchievementIconsDir, IconName(url));
                    File.WriteAllBytes(dest + ".part", bytes);
                    File.Move(dest + ".part", dest, overwrite: true);
                    got++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { Log.Info($"Achievements: icon {url} skipped: {ex.Message}"); }
                try { await Task.Delay(60, ct); } catch (TaskCanceledException) { break; }
            }
            if (got > 0) IconsCached?.Invoke(set.GameId);
        }
        finally { _iconGate.Release(); }
    }

    public void Dispose()
    {
        _launcher.SessionEnded -= OnSessionEnded;
        _cts.Cancel();
    }
}

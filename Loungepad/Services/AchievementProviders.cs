using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Loungepad.Models;

namespace Loungepad.Services;

/// <summary>
/// Somewhere a game's achievements come from. One per store, each asking by the id the store
/// already gave the library entry -- Steam's app id, Xbox's title id, Epic's namespace, GOG's
/// product id -- so, like the metadata, nothing here matches on titles. The one exception is
/// RetroAchievements, which identifies a ROM by its hash and falls back to its title.
/// </summary>
public interface IAchievementProvider
{
    /// <summary>steam | xbox | epic | gog | retro -- what the page shows as the source.</summary>
    string Source { get; }
    /// <summary>This game is the kind this provider answers for, sign-in or not. What decides
    /// whether the page offers an Achievements button at all.</summary>
    bool Supports(Game game);
    /// <summary>Why nothing would come back right now -- not signed in, no key -- in words for the
    /// page, or null when a fetch can be tried.</summary>
    string? Blocked(Game game);
    /// <summary>The game's achievements. A set with Error set and no Items is "could not ask";
    /// a set with no Items and no Error is a game that has none. Never throws.</summary>
    Task<GameAchievements> FetchAsync(Game game, GameAchievements? previous, CancellationToken ct);
    /// <summary>True once the source has refused in a way that will repeat for every game this
    /// pass -- a rate limit, an unreachable service -- so the pass stops asking.</summary>
    bool Unavailable { get; }
    /// <summary>A new pass: forget the refusal and try again.</summary>
    void ResetPass();
}

/// <summary>The bits every provider needs.</summary>
internal static class AchievementJson
{
    public static readonly HttpClient Http = MakeClient();

    private static HttpClient MakeClient()
    {
        var c = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        c.DefaultRequestHeaders.Add("User-Agent", "Loungepad/1.0 (+https://github.com/sukumar-v/Loungepad)");
        return c;
    }

    public static string? Str(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static bool Bool(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v)
        && (v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) && n != 0));

    public static JsonElement? Obj(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    public static JsonElement? Arr(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array ? v : null;

    public static DateTime? Unix(long? secs) =>
        secs is > 0 ? DateTimeOffset.FromUnixTimeSeconds(secs.Value).LocalDateTime : null;

    public static DateTime? Iso(string? s)
    {
        if (s is null) return null;
        if (!DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d)) return null;
        // "0001-01-01" is Xbox's "not yet".
        return d.Year < 1990 ? null : d.ToLocalTime();
    }

    public static GameAchievements Failed(Game game, string source, GameAchievements? previous, string error)
    {
        // A failed ask keeps whatever was fetched before; only the error line changes.
        var set = previous ?? new GameAchievements { GameId = game.Id, Source = source };
        set.Error = error;
        return set;
    }

    public static GameAchievements Fresh(Game game, string source, GameAchievements? previous, List<Achievement> items)
    {
        return new GameAchievements
        {
            GameId = game.Id, Source = source, Items = items, FetchedAt = DateTime.UtcNow, Error = null,
            Version = GameAchievements.CurrentVersion,
            SourceGameId = previous?.SourceGameId, SourceName = previous?.SourceName,
        };
    }
}

// ============================================================================ Steam

/// <summary>
/// Steam, by app id, straight to Steam and nowhere else. The shared metadata service used to carry
/// this with a key of its own, which meant the SteamID and the unlock list of everyone who had not
/// set a key passed through it on every start. It no longer does: the account's data goes only to
/// Steam, and a launcher with neither a key nor a sign-in fetches no achievements.
///
/// Two asks. The list -- names, descriptions, icons, hidden, and the share of players who have each
/// -- is IPlayerService/GetGameAchievements, which takes no key or token at all. The player's
/// unlocks are ISteamUserStats/GetPlayerAchievements with the user's own key, or, with a Steam
/// sign-in, the account's Community stats page (?xml=1), which carries every unlock's time.
///
/// The sign-in's token does NOT work here. ISteamUserStats does not know `access_token=` at all
/// (it answers 400 "Required parameter 'key' is missing", where GetOwnedGames answers a bad token
/// with a 401), and that 400 used to be read as "this game has no achievements" -- so signing in
/// saved an empty list over every Steam game's real one (Oct 6 2026). The Community page needs no
/// token for a profile whose game details are public; a private one says so in the row's hint.
/// </summary>
public sealed class SteamAchievementProvider : IAchievementProvider
{
    private readonly Func<AppSettings> _settings;
    private readonly SteamWebSession? _web;
    public string Source => "steam";
    public bool Unavailable { get; private set; }

    public SteamAchievementProvider(Func<AppSettings> settings, SteamWebSession? web = null)
    {
        _settings = settings;
        _web = web;
    }

    public void ResetPass() => Unavailable = false;

    public bool Supports(Game g) => !g.Emulated && g.Platform == "Steam" && g.Id.StartsWith("steam:", StringComparison.Ordinal);

    private const string NeedsAuth = "Sign in to Steam or add a Steam Web API key under Settings → Library. Achievements are read from Steam with your own sign-in or key only";

    public string? Blocked(Game g)
    {
        var s = _settings();
        if (s.SteamApiKey.Trim().Length > 0)
            return SteamAccountService.DetectAccount() is null ? "No Steam login was found on this PC" : null;
        return _web is { SignedIn: true } ? null : NeedsAuth;
    }

    public async Task<GameAchievements> FetchAsync(Game game, GameAchievements? previous, CancellationToken ct)
    {
        var appId = game.Id["steam:".Length..];
        var key = _settings().SteamApiKey.Trim();
        string steamId;
        if (key.Length > 0)
        {
            var account = SteamAccountService.DetectAccount();
            if (account is null) return AchievementJson.Failed(game, Source, previous, "No Steam login was found on this PC");
            steamId = account.SteamId;
        }
        // The sign-in only says whose account it is: the Community page is read without its token.
        else if (_web is { SignedIn: true, SteamId: { Length: > 0 } signedIn }) steamId = signedIn;
        else return AchievementJson.Failed(game, Source, previous, NeedsAuth);

        try
        {
            return await DirectAsync(game, previous, appId, steamId, key.Length > 0 ? key : null, ct);
        }
        catch (Exception ex)
        {
            Log.Info($"Steam achievements for {game.Title} failed: {ex.Message}");
            Unavailable = ex is HttpRequestException or TaskCanceledException;
            return AchievementJson.Failed(game, Source, previous, "Could not reach Steam");
        }
    }

    private const string PrivateProfile = "Steam shows this account's achievements to its owner only. Set Game details to Public in Steam's privacy settings, or add a Steam Web API key under Settings → Library";

    private async Task<GameAchievements> DirectAsync(Game game, GameAchievements? previous, string appId, string steamId, string? key, CancellationToken ct)
    {
        // The list, keyless: every achievement the game has, its names, icons and how many players
        // have it. "{"response":{}}" is a game with none -- the one answer that may say so.
        var listUrl = $"https://api.steampowered.com/IPlayerService/GetGameAchievements/v1/?appid={appId}&language=english";
        using var listRes = await AchievementJson.Http.GetAsync(listUrl, ct);
        if (!listRes.IsSuccessStatusCode)
        {
            var code = (int)listRes.StatusCode;
            Log.Info($"Steam achievements: {code} for the list of {appId}");
            Unavailable = code == 429 || code >= 500;
            return AchievementJson.Failed(game, Source, previous, $"Steam did not answer ({code})");
        }

        var items = new List<Achievement>();
        // Case-blind: the list says "CHARMED" and the Community page "charmed" for the same one.
        var byId = new Dictionary<string, Achievement>(StringComparer.OrdinalIgnoreCase);
        using (var doc = JsonDocument.Parse(await listRes.Content.ReadAsStringAsync(ct)))
        {
            var resp = AchievementJson.Obj(doc.RootElement, "response");
            if (resp is { } r && AchievementJson.Arr(r, "achievements") is { } arr)
                foreach (var a in arr.EnumerateArray())
                {
                    var id = AchievementJson.Str(a, "internal_name");
                    // Archived ones are retired from the game; Steam's own pages do not list them.
                    if (string.IsNullOrEmpty(id) || AchievementJson.Bool(a, "archived") || byId.ContainsKey(id)) continue;
                    var pct = double.TryParse(AchievementJson.Str(a, "player_percent_unlocked"), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : (double?)null;
                    var ach = new Achievement
                    {
                        Id = id,
                        Name = AchievementJson.Str(a, "localized_name") ?? id,
                        Description = AchievementJson.Str(a, "localized_desc"),
                        Hidden = AchievementJson.Bool(a, "hidden"),
                        IconUrl = IconUrl(appId, AchievementJson.Str(a, "icon")),
                        IconLockedUrl = IconUrl(appId, AchievementJson.Str(a, "icon_gray")),
                        Percent = pct,
                    };
                    items.Add(ach);
                    byId[id] = ach;
                }
        }
        if (items.Count == 0) return AchievementJson.Fresh(game, Source, previous, items);

        return key is not null
            ? await UnlocksByKeyAsync(game, previous, appId, steamId, key, items, byId, ct)
            : await UnlocksByCommunityAsync(game, previous, appId, steamId, items, byId, ct);
    }

    /// <summary>The icon file names the list gives, as the URLs Steam's own Community page uses.</summary>
    private static string? IconUrl(string appId, string? file) =>
        string.IsNullOrEmpty(file) ? null : $"https://shared.akamai.steamstatic.com/community_assets/images/apps/{appId}/{file}";

    private async Task<GameAchievements> UnlocksByKeyAsync(Game game, GameAchievements? previous, string appId, string steamId, string key,
        List<Achievement> items, Dictionary<string, Achievement> byId, CancellationToken ct)
    {
        // Steam answers 403 "Profile is not public" here for an app the account has never started
        // or does not own, on a public profile as much as a private one -- and this is the account's
        // own key on the account's own profile, which a privacy setting cannot keep out. So a 403 is
        // "nothing unlocked", never "private". A 401 is the key.
        var playerUrl = $"https://api.steampowered.com/ISteamUserStats/GetPlayerAchievements/v1/?key={Uri.EscapeDataString(key)}&steamid={steamId}&appid={appId}";
        using var playerRes = await AchievementJson.Http.GetAsync(playerUrl, ct);
        if (playerRes.StatusCode == HttpStatusCode.Unauthorized)
        {
            Log.Info("Steam achievements: 401 for the unlocks through the key");
            return AchievementJson.Failed(game, Source, previous, "Steam rejected the Web API key");
        }
        if (playerRes.IsSuccessStatusCode)
        {
            using var doc = JsonDocument.Parse(await playerRes.Content.ReadAsStringAsync(ct));
            var ps = AchievementJson.Obj(doc.RootElement, "playerstats");
            if (ps is { } p && AchievementJson.Arr(p, "achievements") is { } arr)
                foreach (var a in arr.EnumerateArray())
                {
                    var id = AchievementJson.Str(a, "apiname");
                    if (id is null || !byId.TryGetValue(id, out var ach)) continue;
                    ach.Unlocked = AchievementJson.Bool(a, "achieved");
                    ach.UnlockedAt = ach.Unlocked ? AchievementJson.Unix(JsonNum.Long(a, "unlocktime")) : null;
                }
        }
        else if (playerRes.StatusCode != HttpStatusCode.Forbidden)
        {
            var code = (int)playerRes.StatusCode;
            Unavailable = code == 429 || code >= 500;
            return AchievementJson.Failed(game, Source, previous, $"Steam did not answer ({code})");
        }
        return AchievementJson.Fresh(game, Source, previous, items);
    }

    /// <summary>
    /// The unlocks off the account's Community stats page, which carries each one's time and needs
    /// no key for a profile whose game details are public. Three answers: the stats as XML; an HTML
    /// page instead, which is Steam having no stats for this account in this game (owned and never
    /// started); or an error. Anything that is not plainly the first two keeps the previous list --
    /// and so does "no stats" for a game this account had unlocks in, since that cannot be true.
    /// </summary>
    private async Task<GameAchievements> UnlocksByCommunityAsync(Game game, GameAchievements? previous, string appId, string steamId,
        List<Achievement> items, Dictionary<string, Achievement> byId, CancellationToken ct)
    {
        var url = $"https://steamcommunity.com/profiles/{steamId}/stats/{appId}/achievements/?xml=1&l=english";
        using var res = await AchievementJson.Http.GetAsync(url, ct);
        var code = (int)res.StatusCode;
        if (!res.IsSuccessStatusCode)
        {
            Log.Info($"Steam achievements: {code} from the Community page for {appId}");
            Unavailable = code == 429 || code >= 500;
            return AchievementJson.Failed(game, Source, previous, $"Steam Community did not answer ({code})");
        }
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!body.TrimStart().StartsWith("<?xml", StringComparison.Ordinal))
        {
            if (previous is not null && previous.Items.Any(a => a.Unlocked))
                return AchievementJson.Failed(game, Source, previous, "Steam Community has no stats for this game right now");
            return AchievementJson.Fresh(game, Source, previous, items);
        }

        System.Xml.Linq.XDocument xml;
        try { xml = System.Xml.Linq.XDocument.Parse(body); }
        catch (System.Xml.XmlException) { return AchievementJson.Failed(game, Source, previous, "Steam Community answered something unreadable"); }
        var root = xml.Root;
        var list = root?.Element("achievements");
        if (root is null || root.Name != "playerstats" || list is null)
        {
            var why = root?.Element("error")?.Value?.Trim();
            var priv = (root?.Element("privacyState")?.Value ?? "").Trim();
            Log.Info($"Steam achievements: no stats on the Community page for {appId}" + (why is { Length: > 0 } ? $" ({why})" : priv.Length > 0 ? $" (privacy {priv})" : ""));
            return AchievementJson.Failed(game, Source, previous,
                (priv.Length > 0 && priv != "public") || (why ?? "").Contains("private", StringComparison.OrdinalIgnoreCase)
                    ? PrivateProfile : "Steam Community did not show this game's stats");
        }
        foreach (var a in list.Elements("achievement"))
        {
            var id = a.Element("apiname")?.Value?.Trim();
            if (string.IsNullOrEmpty(id) || !byId.TryGetValue(id, out var ach)) continue;
            ach.Unlocked = (string?)a.Attribute("closed") == "1";
            ach.UnlockedAt = ach.Unlocked && long.TryParse(a.Element("unlockTimestamp")?.Value, out var secs) ? AchievementJson.Unix(secs) : null;
        }
        return AchievementJson.Fresh(game, Source, previous, items);
    }
}

// ============================================================================ Xbox

/// <summary>
/// Xbox Live's achievements service, by title id, with the account's XSTS token. Contract
/// version 2 is the Xbox One / Series and PC list; 360 titles live behind version 1 and are not
/// asked for, since nothing on a PC library is one. The rarity block Xbox adds to each entry is
/// the same percentage Steam publishes, so the chips read the same.
/// </summary>
public sealed class XboxAchievementProvider : IAchievementProvider
{
    private readonly XboxAccountClient _client;
    public string Source => "xbox";
    public bool Unavailable { get; private set; }

    public XboxAchievementProvider(XboxAccountClient client) { _client = client; }
    public void ResetPass() => Unavailable = false;

    public bool Supports(Game g) => !g.Emulated && g.Platform == "Xbox" && !g.Id.StartsWith("xbox:store:", StringComparison.Ordinal);

    public string? Blocked(Game g)
    {
        if (!_client.Status.SignedIn) return "Sign in to Xbox under Settings → Library to see achievements";
        if ((g.XboxTitleId ?? _client.TitleIdFor(g.PackageFamilyName)) is null)
            return "Xbox has not seen this game played on this account yet";
        return null;
    }

    public async Task<GameAchievements> FetchAsync(Game game, GameAchievements? previous, CancellationToken ct)
    {
        if (Blocked(game) is { } why) return AchievementJson.Failed(game, Source, previous, why);
        var titleId = game.XboxTitleId ?? _client.TitleIdFor(game.PackageFamilyName)!;
        var xuid = _client.Xuid;
        if (xuid is null) return AchievementJson.Failed(game, Source, previous, "The Xbox sign-in has expired. Sign in again");
        try
        {
            var items = new List<Achievement>();
            string? continuation = null;
            for (var page = 0; page < 20; page++)
            {
                var url = $"https://achievements.xboxlive.com/users/xuid({xuid})/achievements?titleId={Uri.EscapeDataString(titleId)}&maxItems=1000"
                          + (continuation is null ? "" : "&continuationToken=" + Uri.EscapeDataString(continuation));
                var ans = await _client.GetAsync(url, 2, ct);
                if (ans is null) return AchievementJson.Failed(game, Source, previous, "The Xbox sign-in has expired. Sign in again");
                var (status, body) = ans.Value;
                if (status == 401 || status == 403) return AchievementJson.Failed(game, Source, previous, "Xbox Live refused the sign-in. Sign in again under Settings → Library");
                if (status == 429) { Unavailable = true; return AchievementJson.Failed(game, Source, previous, "Xbox Live is busy; try again in a minute"); }
                if (status < 200 || status >= 300) return AchievementJson.Failed(game, Source, previous, $"Xbox Live answered {status}");

                using var doc = JsonDocument.Parse(body);
                if (AchievementJson.Arr(doc.RootElement, "achievements") is { } arr)
                    foreach (var a in arr.EnumerateArray())
                    {
                        var id = AchievementJson.Str(a, "id");
                        if (string.IsNullOrEmpty(id)) continue;
                        var unlocked = string.Equals(AchievementJson.Str(a, "progressState"), "Achieved", StringComparison.OrdinalIgnoreCase);
                        int? score = null;
                        if (AchievementJson.Arr(a, "rewards") is { } rewards)
                            foreach (var r in rewards.EnumerateArray())
                                if (string.Equals(AchievementJson.Str(r, "type"), "Gamerscore", StringComparison.OrdinalIgnoreCase)
                                    && int.TryParse(AchievementJson.Str(r, "value"), out var gs)) score = gs;
                        string? icon = null;
                        if (AchievementJson.Arr(a, "mediaAssets") is { } media)
                            foreach (var m in media.EnumerateArray())
                                if (AchievementJson.Str(m, "url") is { Length: > 0 } u && (icon is null || string.Equals(AchievementJson.Str(m, "type"), "Icon", StringComparison.OrdinalIgnoreCase)))
                                    icon = u;
                        var secret = AchievementJson.Bool(a, "isSecret");
                        items.Add(new Achievement
                        {
                            Id = id,
                            Name = AchievementJson.Str(a, "name") ?? id,
                            // Locked, a secret achievement's description is the locked one Xbox writes for it.
                            Description = unlocked || !secret ? AchievementJson.Str(a, "description") : (AchievementJson.Str(a, "lockedDescription") ?? AchievementJson.Str(a, "description")),
                            Hidden = secret,
                            Unlocked = unlocked,
                            UnlockedAt = unlocked ? AchievementJson.Iso(AchievementJson.Str(AchievementJson.Obj(a, "progression") ?? default, "timeUnlocked")) : null,
                            IconUrl = icon,
                            Score = score,
                            Percent = AchievementJson.Obj(a, "rarity") is { } rar ? JsonNum.Double(rar, "currentPercentage") : null,
                        });
                    }
                continuation = AchievementJson.Obj(doc.RootElement, "pagingInfo") is { } pi ? AchievementJson.Str(pi, "continuationToken") : null;
                if (string.IsNullOrEmpty(continuation)) break;
            }
            var set = AchievementJson.Fresh(game, Source, previous, items);
            set.SourceGameId = titleId;
            return set;
        }
        catch (Exception ex)
        {
            Log.Info($"Xbox achievements for {game.Title} failed: {ex.Message}");
            Unavailable = ex is HttpRequestException or TaskCanceledException;
            return AchievementJson.Failed(game, Source, previous, "Could not reach Xbox Live");
        }
    }
}

// ============================================================================ Epic

/// <summary>
/// The Epic Games Store's GraphQL, by the game's catalogue namespace ("sandbox"): the schema
/// with names, icons and rarity is public, the player's unlocks need the launcher's bearer
/// token, which the sign-in already holds. The two queries are the ones the store's own web
/// pages send.
/// </summary>
public sealed class EpicAchievementProvider : IAchievementProvider
{
    private const string GraphQl = "https://launcher.store.epicgames.com/graphql";
    private readonly EpicAccountClient _client;
    public string Source => "epic";
    public bool Unavailable { get; private set; }

    public EpicAchievementProvider(EpicAccountClient client) { _client = client; }
    public void ResetPass() => Unavailable = false;

    public bool Supports(Game g) => !g.Emulated && g.Platform == "Epic" && !string.IsNullOrEmpty(g.EpicNamespace);

    public string? Blocked(Game g) =>
        _client.Status.SignedIn ? null : "Sign in to Epic under Settings → Library to see achievements";

    private const string SchemaQuery = @"query Achievement($SandboxId: String!, $Locale: String!) {
  Achievement {
    productAchievementsRecordBySandbox(sandboxId: $SandboxId, locale: $Locale) {
      productId
      achievements { achievement {
        name hidden unlockedDisplayName lockedDisplayName unlockedDescription lockedDescription
        unlockedIconLink lockedIconLink XP rarity { percent }
      } }
    }
  }
}";

    private const string PlayerQuery = @"query playerProfileAchievementsByProductId($EpicAccountId: String!, $ProductId: String!) {
  PlayerProfile {
    playerProfile(epicAccountId: $EpicAccountId) {
      productAchievements(productId: $ProductId) {
        ... on PlayerProductAchievementsResponseSuccess {
          data { playerAchievements { playerAchievement { achievementName unlocked unlockDate } } }
        }
      }
    }
  }
}";

    public async Task<GameAchievements> FetchAsync(Game game, GameAchievements? previous, CancellationToken ct)
    {
        if (Blocked(game) is { } why) return AchievementJson.Failed(game, Source, previous, why);
        try
        {
            var token = await _client.AccessTokenAsync(ct);
            var accountId = _client.AccountId;
            if (token is null || accountId is null) return AchievementJson.Failed(game, Source, previous, "The Epic sign-in has expired. Sign in again under Settings → Library");

            using var schema = await QueryAsync(token, "Achievement", SchemaQuery, new { SandboxId = game.EpicNamespace, Locale = "en" }, ct);
            if (schema is null) return AchievementJson.Failed(game, Source, previous, "Epic did not answer");
            var record = AchievementJson.Obj(AchievementJson.Obj(AchievementJson.Obj(schema.RootElement, "data") ?? default, "Achievement") ?? default, "productAchievementsRecordBySandbox");
            var items = new List<Achievement>();
            var byId = new Dictionary<string, Achievement>(StringComparer.Ordinal);
            var productId = record is { } rec ? AchievementJson.Str(rec, "productId") : null;
            if (record is { } r && AchievementJson.Arr(r, "achievements") is { } arr)
                foreach (var wrap in arr.EnumerateArray())
                {
                    if (AchievementJson.Obj(wrap, "achievement") is not { } a) continue;
                    var id = AchievementJson.Str(a, "name");
                    if (string.IsNullOrEmpty(id)) continue;
                    var ach = new Achievement
                    {
                        Id = id,
                        Name = AchievementJson.Str(a, "unlockedDisplayName") ?? AchievementJson.Str(a, "lockedDisplayName") ?? id,
                        Description = AchievementJson.Str(a, "unlockedDescription") ?? AchievementJson.Str(a, "lockedDescription"),
                        Hidden = AchievementJson.Bool(a, "hidden"),
                        IconUrl = AchievementJson.Str(a, "unlockedIconLink"),
                        IconLockedUrl = AchievementJson.Str(a, "lockedIconLink"),
                        Score = JsonNum.Int(a, "XP"),
                        Percent = AchievementJson.Obj(a, "rarity") is { } rar ? JsonNum.Double(rar, "percent") : null,
                    };
                    items.Add(ach);
                    byId[id] = ach;
                }
            if (items.Count == 0 || productId is null) return AchievementJson.Fresh(game, Source, previous, items);

            using var player = await QueryAsync(token, "playerProfileAchievementsByProductId", PlayerQuery, new { EpicAccountId = accountId, ProductId = productId }, ct);
            if (player is not null)
            {
                var data = AchievementJson.Obj(AchievementJson.Obj(AchievementJson.Obj(AchievementJson.Obj(AchievementJson.Obj(player.RootElement, "data") ?? default, "PlayerProfile") ?? default, "playerProfile") ?? default, "productAchievements") ?? default, "data");
                if (data is { } d && AchievementJson.Arr(d, "playerAchievements") is { } pa)
                    foreach (var wrap in pa.EnumerateArray())
                    {
                        if (AchievementJson.Obj(wrap, "playerAchievement") is not { } p) continue;
                        var id = AchievementJson.Str(p, "achievementName");
                        if (id is null || !byId.TryGetValue(id, out var ach)) continue;
                        ach.Unlocked = AchievementJson.Bool(p, "unlocked");
                        ach.UnlockedAt = ach.Unlocked ? AchievementJson.Iso(AchievementJson.Str(p, "unlockDate")) : null;
                    }
            }
            var set = AchievementJson.Fresh(game, Source, previous, items);
            set.SourceGameId = productId;
            return set;
        }
        catch (Exception ex)
        {
            Log.Info($"Epic achievements for {game.Title} failed: {ex.Message}");
            Unavailable = ex is HttpRequestException or TaskCanceledException;
            return AchievementJson.Failed(game, Source, previous, "Could not reach Epic");
        }
    }

    private static async Task<JsonDocument?> QueryAsync(string token, string operation, string query, object variables, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new { operationName = operation, query, variables });
        using var req = new HttpRequestMessage(HttpMethod.Post, GraphQl) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        req.Headers.TryAddWithoutValidation("Authorization", "bearer " + token);
        using var res = await AchievementJson.Http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            Log.Info($"Epic: GraphQL {operation} answered {(int)res.StatusCode}: {(text.Length > 200 ? text[..200] : text)}");
            return null;
        }
        return JsonDocument.Parse(text);
    }
}

// ============================================================================ GOG

/// <summary>
/// GOG's gameplay service, by product id, with the bearer token the account page issues to a
/// signed-in session -- the same token Galaxy uses, without Galaxy's dead OAuth client. Each
/// game is a "client" to that service, hence the URL.
/// </summary>
public sealed class GogAchievementProvider : IAchievementProvider
{
    private readonly GogAccountClient _client;
    public string Source => "gog";
    public bool Unavailable { get; private set; }

    public GogAchievementProvider(GogAccountClient client) { _client = client; }
    public void ResetPass() => Unavailable = false;

    public bool Supports(Game g) => !g.Emulated && g.Platform == "GOG" && g.Id.StartsWith("gog:", StringComparison.Ordinal)
                                    && g.Id["gog:".Length..].All(char.IsDigit);

    public string? Blocked(Game g) =>
        _client.Status.SignedIn ? null : "Sign in to GOG under Settings → Library to see achievements";

    public async Task<GameAchievements> FetchAsync(Game game, GameAchievements? previous, CancellationToken ct)
    {
        if (Blocked(game) is { } why) return AchievementJson.Failed(game, Source, previous, why);
        try
        {
            var auth = await _client.GameplayAuthAsync(ct);
            if (auth is null) return AchievementJson.Failed(game, Source, previous, "The GOG session has expired. Sign in again under Settings → Library");
            var (userId, token) = auth.Value;
            var productId = game.Id["gog:".Length..];
            var items = new List<Achievement>();
            // GOG hands back a page_token on the last page too, and the same list again for it:
            // walking the tokens to the 20-page cap stored Cyberpunk's 57 achievements twenty times
            // over (Oct 6 2026). So an achievement is taken once, and a page with nothing new ends it.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string? pageToken = null;
            for (var page = 0; page < 20; page++)
            {
                var url = $"https://gameplay.gog.com/clients/{productId}/users/{userId}/achievements" + (pageToken is null ? "" : "?page_token=" + Uri.EscapeDataString(pageToken));
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                req.Headers.TryAddWithoutValidation("Accept-Language", "en-US");
                using var res = await AchievementJson.Http.SendAsync(req, ct);
                var status = (int)res.StatusCode;
                // A game with no achievements is a 404 here; a 401 is the token, which the next
                // ask refreshes.
                if (status == 404) break;
                if (status == 401 || status == 403) return AchievementJson.Failed(game, Source, previous, "GOG refused the session. Sign in again under Settings → Library");
                if (status == 429) { Unavailable = true; return AchievementJson.Failed(game, Source, previous, "GOG is busy; try again in a minute"); }
                if (!res.IsSuccessStatusCode) return AchievementJson.Failed(game, Source, previous, $"GOG answered {status}");

                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
                var added = 0;
                if (AchievementJson.Arr(doc.RootElement, "items") is { } arr)
                    foreach (var a in arr.EnumerateArray())
                    {
                        var id = AchievementJson.Str(a, "achievement_key") ?? AchievementJson.Str(a, "achievement_id") ?? AchievementJson.Str(a, "id");
                        if (string.IsNullOrEmpty(id) || !seen.Add(id)) continue;
                        added++;
                        var when = AchievementJson.Iso(AchievementJson.Str(a, "date_unlocked"));
                        items.Add(new Achievement
                        {
                            Id = id,
                            Name = (AchievementJson.Str(a, "name") ?? id).Trim(),
                            Description = AchievementJson.Str(a, "description")?.Trim(),
                            Hidden = !AchievementJson.Bool(a, "visible"),
                            Unlocked = when is not null,
                            UnlockedAt = when,
                            IconUrl = AchievementJson.Str(a, "image_url_unlocked"),
                            IconLockedUrl = AchievementJson.Str(a, "image_url_locked"),
                            Percent = JsonNum.Double(a, "rarity"),
                        });
                    }
                var next = AchievementJson.Str(doc.RootElement, "page_token");
                if (string.IsNullOrEmpty(next) || next == pageToken || added == 0) break;
                pageToken = next;
            }
            return AchievementJson.Fresh(game, Source, previous, items);
        }
        catch (Exception ex)
        {
            Log.Info($"GOG achievements for {game.Title} failed: {ex.Message}");
            Unavailable = ex is HttpRequestException or TaskCanceledException;
            return AchievementJson.Failed(game, Source, previous, "Could not reach GOG");
        }
    }
}

// ============================================================================ RetroAchievements

/// <summary>
/// RetroAchievements, for emulated games, with the user's own username and web API key. The
/// game is found by the ROM's hash against the console's game list (RetroHash), or by title when
/// the system is one this cannot hash; the answer names the RetroAchievements game so the page
/// can say what it matched. The list per console is a few hundred kilobytes and changes weekly,
/// so it is kept for five days.
/// </summary>
public sealed class RetroAchievementProvider : IAchievementProvider
{
    private const string Api = "https://retroachievements.org/API/";
    private readonly Func<AppSettings> _settings;
    private readonly string _cacheDir;
    public string Source => "retro";
    public bool Unavailable { get; private set; }

    public RetroAchievementProvider(Func<AppSettings> settings, string cacheDir)
    {
        _settings = settings;
        _cacheDir = cacheDir;
    }
    public void ResetPass() => Unavailable = false;

    /// <summary>Our platform ids against RetroAchievements' console ids (API_GetConsoleIDs).</summary>
    public static readonly IReadOnlyDictionary<string, int> Consoles = new Dictionary<string, int>
    {
        ["genesis"] = 1, ["n64"] = 2, ["snes"] = 3, ["gb"] = 4, ["gba"] = 5, ["gbc"] = 6, ["nes"] = 7, ["tg16"] = 8,
        ["segacd"] = 9, ["32x"] = 10, ["sms"] = 11, ["ps1"] = 12, ["lynx"] = 13, ["ngp"] = 14, ["gg"] = 15, ["gc"] = 16,
        ["jaguar"] = 17, ["nds"] = 18, ["ps2"] = 21, ["atari2600"] = 25, ["arcade"] = 27, ["neogeo"] = 27, ["vb"] = 28,
        ["msx"] = 29, ["saturn"] = 39, ["dreamcast"] = 40, ["psp"] = 41, ["3do"] = 43, ["coleco"] = 44, ["intv"] = 45,
        ["atari7800"] = 51, ["ws"] = 53, ["tgcd"] = 76,
    };

    public bool Supports(Game g) => g.Emulated && g.PlatformId is { } p && Consoles.ContainsKey(p);

    public string? Blocked(Game g)
    {
        var s = _settings();
        return s.RetroAchievementsUser.Trim().Length == 0 || s.RetroAchievementsKey.Trim().Length == 0
            ? "Add your RetroAchievements username and web API key under Settings → Stats"
            : null;
    }

    public async Task<GameAchievements> FetchAsync(Game game, GameAchievements? previous, CancellationToken ct)
    {
        if (Blocked(game) is { } why) return AchievementJson.Failed(game, Source, previous, why);
        var s = _settings();
        var user = s.RetroAchievementsUser.Trim();
        var key = s.RetroAchievementsKey.Trim();
        var console = Consoles[game.PlatformId!];
        try
        {
            var (raId, raTitle) = await ResolveAsync(game, previous, console, key, ct);
            if (raId is null)
            {
                // Not on RetroAchievements, or not matched: that is an answer, and one worth
                // keeping for a while rather than hashing the file on every pass.
                var none = AchievementJson.Fresh(game, Source, previous, new List<Achievement>());
                none.SourceName = null;
                return none;
            }

            var url = $"{Api}API_GetGameInfoAndUserProgress.php?y={Uri.EscapeDataString(key)}&u={Uri.EscapeDataString(user)}&g={raId}";
            using var res = await AchievementJson.Http.GetAsync(url, ct);
            var status = (int)res.StatusCode;
            if (status == 401 || status == 403) return AchievementJson.Failed(game, Source, previous, "RetroAchievements rejected the username or key");
            if (status == 429) { Unavailable = true; return AchievementJson.Failed(game, Source, previous, "RetroAchievements is busy; try again in a minute"); }
            if (!res.IsSuccessStatusCode) return AchievementJson.Failed(game, Source, previous, $"RetroAchievements answered {status}");
            var body = await res.Content.ReadAsStringAsync(ct);
            if (body.Contains("Unauthenticated", StringComparison.OrdinalIgnoreCase)) return AchievementJson.Failed(game, Source, previous, "RetroAchievements rejected the username or key");

            var items = new List<Achievement>();
            var order = new Dictionary<string, int>(StringComparer.Ordinal);
            using (var doc = JsonDocument.Parse(body))
            {
                var root = doc.RootElement;
                var players = JsonNum.Int(root, "NumDistinctPlayers") ?? JsonNum.Int(root, "NumDistinctPlayersCasual") ?? 0;
                raTitle = AchievementJson.Str(root, "Title") ?? raTitle;
                if (AchievementJson.Obj(root, "Achievements") is { } achs)
                    foreach (var prop in achs.EnumerateObject())
                    {
                        var a = prop.Value;
                        var id = JsonNum.Long(a, "ID")?.ToString() ?? prop.Name;
                        var earned = AchievementJson.Str(a, "DateEarnedHardcore") ?? AchievementJson.Str(a, "DateEarned");
                        var awarded = JsonNum.Int(a, "NumAwarded") ?? 0;
                        var badge = AchievementJson.Str(a, "BadgeName");
                        order[id] = JsonNum.Int(a, "DisplayOrder") ?? items.Count;
                        items.Add(new Achievement
                        {
                            Id = id,
                            Name = AchievementJson.Str(a, "Title") ?? id,
                            Description = AchievementJson.Str(a, "Description"),
                            Unlocked = earned is not null,
                            // RetroAchievements writes UTC without saying so.
                            UnlockedAt = earned is not null && DateTime.TryParse(earned, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var d) ? d.ToLocalTime() : null,
                            IconUrl = badge is null ? null : $"https://media.retroachievements.org/Badge/{badge}.png",
                            IconLockedUrl = badge is null ? null : $"https://media.retroachievements.org/Badge/{badge}_lock.png",
                            Score = JsonNum.Int(a, "Points"),
                            Percent = players > 0 ? Math.Round(100.0 * awarded / players, 1) : null,
                        });
                    }
            }
            // In the order the site lists them.
            items = items.OrderBy(a => order.GetValueOrDefault(a.Id)).ToList();
            var set = AchievementJson.Fresh(game, Source, previous, items);
            set.SourceGameId = raId;
            set.SourceName = raTitle;
            return set;
        }
        catch (Exception ex)
        {
            Log.Info($"RetroAchievements for {game.Title} failed: {ex.Message}");
            Unavailable = ex is HttpRequestException or TaskCanceledException;
            return AchievementJson.Failed(game, Source, previous, "Could not reach RetroAchievements");
        }
    }

    private sealed record RaGame(string Id, string Title, List<string> Hashes);

    /// <summary>The RetroAchievements game id: the one cached on the last set, else by hash, else
    /// by title against the console's list.</summary>
    private async Task<(string? Id, string? Title)> ResolveAsync(Game game, GameAchievements? previous, int console, string key, CancellationToken ct)
    {
        if (previous?.SourceGameId is { Length: > 0 } cached && previous.Source == Source) return (cached, previous.SourceName);
        var list = await GameListAsync(console, key, ct);
        if (list is null) return (null, null);

        if (game.RomPath is { } rom && game.PlatformId is { } pid)
        {
            var hash = await Task.Run(() => RetroHash.Compute(rom, pid), ct);
            if (hash is not null)
            {
                var hit = list.FirstOrDefault(g => g.Hashes.Any(h => h.Equals(hash, StringComparison.OrdinalIgnoreCase)));
                if (hit is not null) { Log.Info($"RetroAchievements: {game.Title} matched by hash to {hit.Title} ({hit.Id})"); return (hit.Id, hit.Title); }
            }
        }
        // By title, and only an exact one after folding: a wrong set is worse than none, because
        // nothing about it looks wrong.
        var byTitle = list.Where(g => TitleMatch.IsConfident(game.Title, CleanRaTitle(g.Title))).ToList();
        if (byTitle.Count == 1) { Log.Info($"RetroAchievements: {game.Title} matched by title to {byTitle[0].Title} ({byTitle[0].Id})"); return (byTitle[0].Id, byTitle[0].Title); }
        return (null, null);
    }

    /// <summary>RetroAchievements prefixes hacks and homebrew ("~Hack~ Title") and suffixes
    /// subsets ("Title | Subset"); neither is part of the game's name.</summary>
    public static string CleanRaTitle(string t)
    {
        var s = t;
        while (s.StartsWith('~')) { var end = s.IndexOf('~', 1); if (end < 0) break; s = s[(end + 1)..].TrimStart(); }
        var bar = s.IndexOf(" | ", StringComparison.Ordinal);
        if (bar > 0) s = s[..bar];
        return s.Trim();
    }

    /// <summary>Every game with achievements on the console, with its hashes. Cached for five days.</summary>
    private async Task<List<RaGame>?> GameListAsync(int console, string key, CancellationToken ct)
    {
        var file = Path.Combine(_cacheDir, $"ra-games-{console}.json");
        string? text = null;
        try
        {
            if (File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < TimeSpan.FromDays(5)) text = File.ReadAllText(file);
        }
        catch { /* re-fetch */ }
        if (text is null)
        {
            using var res = await AchievementJson.Http.GetAsync($"{Api}API_GetGameList.php?y={Uri.EscapeDataString(key)}&i={console}&f=1&h=1", ct);
            if (!res.IsSuccessStatusCode) { Log.Info($"RetroAchievements: game list for console {console} answered {(int)res.StatusCode}"); return null; }
            text = await res.Content.ReadAsStringAsync(ct);
            if (!text.TrimStart().StartsWith('[')) { Log.Info("RetroAchievements: game list was not a list (bad key?)"); return null; }
            try { Directory.CreateDirectory(_cacheDir); File.WriteAllText(file, text); } catch { /* a cache */ }
        }
        var list = new List<RaGame>();
        using var doc = JsonDocument.Parse(text);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
        foreach (var g in doc.RootElement.EnumerateArray())
        {
            var id = JsonNum.Long(g, "ID")?.ToString();
            var title = AchievementJson.Str(g, "Title");
            if (id is null || title is null) continue;
            var hashes = AchievementJson.Arr(g, "Hashes") is { } hs
                ? hs.EnumerateArray().Where(h => h.ValueKind == JsonValueKind.String).Select(h => h.GetString()!).ToList()
                : new List<string>();
            list.Add(new RaGame(id, title, hashes));
        }
        return list;
    }
}

using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Loungepad.Models;

namespace Loungepad.Services;

/// <summary>A theme or extension found on disk, with what addons.json knows about it.</summary>
public sealed class InstalledAddon
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = AddonKind.Extension;
    public AddonManifest Manifest { get; init; } = new();
    public string Folder { get; init; } = "";
    public AddonRecord? Record { get; init; }
    /// <summary>A theme the launcher ships and keeps up to date itself (ThemeService.SyncBuiltIn):
    /// listed, never removed from here.</summary>
    public bool Bundled { get; init; }
    /// <summary>Set when the manifest could not be read; the add-on is still listed so it can be removed.</summary>
    public string? Error { get; init; }
    public string Key => AddonService.KeyOf(Kind, Id);
    public bool Enabled => Record?.Enabled ?? true;
}

/// <summary>Where an install stands, pushed to the page as it goes.</summary>
public sealed class AddonProgress
{
    public string Key { get; init; } = "";
    /// <summary>downloading | installing | done | failed</summary>
    public string State { get; init; } = "";
    public int Percent { get; init; }
    public string? Error { get; init; }
}

/// <summary>Everything the page draws for one add-on (see addons.js), installed or available.</summary>
public sealed class AddonDto
{
    public string Key { get; set; } = "";
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? Author { get; set; }
    public string? Homepage { get; set; }
    public string? Icon { get; set; }
    public bool Installed { get; set; }
    public bool Bundled { get; set; }
    public bool Enabled { get; set; }
    /// <summary>catalogue | zip | folder | bundled | manual (a folder somebody dropped in)</summary>
    public string? Source { get; set; }
    public bool CanReload { get; set; }
    public string? Version { get; set; }
    /// <summary>The repository's version, when it lists this add-on.</summary>
    public string? Available { get; set; }
    public bool Update { get; set; }
    /// <summary>The repository's counts, or null when the service has not answered (or this add-on
    /// is not one it counts: a bundled theme, a zip or folder install of something unlisted).</summary>
    public long? Downloads { get; set; }
    public long? Likes { get; set; }
    /// <summary>This PC liked it (kept in addons.json, never sent as an identity).</summary>
    public bool Liked { get; set; }
    /// <summary>Whether this launcher may install what the repository has.</summary>
    public bool Installable { get; set; }
    public string? NeedsLauncher { get; set; }
    public List<string> Hosts { get; set; } = new();
    public AddonContributes? Contributes { get; set; }
    public JsonElement? Settings { get; set; }
    public PassSummary? LastPass { get; set; }
    public object? Status { get; set; }
    public string? Busy { get; set; }
    public int Progress { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// The catalogue and the installer behind Settings → Add-ons (docs/ADDONS.md).
///
/// The repository's index.json is read, cached under %LOCALAPPDATA% and refreshed every few hours;
/// installing is downloading each file the index names, checking its SHA-256 against the index,
/// and only then putting the folder where the launcher reads it. A zip or a folder on this PC
/// goes through the same checks minus the hashes. What addons.json keeps is where each install
/// came from and whether it is enabled; the folders are what says what is installed, so a theme
/// dropped in by hand is listed like any other.
///
/// Every folder is a constructor argument, so a harness can run the whole thing against a scratch
/// tree and a local index without touching the real data folder.
/// </summary>
public class AddonService
{
    public const string DefaultIndexUrl = "https://raw.githubusercontent.com/sukumar-v/loungepad-addons/main/index.json";
    public static readonly TimeSpan CatalogueFreshness = TimeSpan.FromHours(6);

    private const int MaxFiles = 200;
    private const long MaxFileBytes = 16L * 1024 * 1024;
    private const long MaxAddonBytes = 64L * 1024 * 1024;
    private const long MaxIconBytes = 512 * 1024;
    private const long MaxIndexBytes = 4L * 1024 * 1024;

    private static readonly JsonSerializerOptions ManifestOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
    private static readonly JsonSerializerOptions FileOpts = new() { WriteIndented = true };

    private static readonly Regex HostPattern = new(@"^(\*\.)?[a-z0-9-]+(\.[a-z0-9-]+)+$", RegexOptions.Compiled);
    private static readonly Regex Sha256Pattern = new("^[0-9a-f]{64}$", RegexOptions.Compiled);
    private static readonly HashSet<string> IconExtensions = new(StringComparer.OrdinalIgnoreCase) { ".svg", ".png", ".jpg", ".jpeg", ".webp" };

    private readonly string _themesDir, _extensionsDir, _recordsFile, _cacheDir, _themeBackups, _extBackups;
    private readonly Func<string?> _indexUrl;
    /// <summary>The metadata service's base URL, which also answers /v1/addons/* (addons-stats/).
    /// Null or empty turns the counts off.</summary>
    private readonly Func<string?> _statsBase;
    private Dictionary<string, AddonCounts>? _counts;
    private DateTime _countsAt;
    private bool _countsBusy;
    public static readonly TimeSpan CountsFreshness = TimeSpan.FromMinutes(5);
    private readonly AddonVersion _launcher;
    private readonly HttpClient _http;
    private readonly object _gate = new();
    private AddonsFile _records = new();
    private CatalogueCache? _catalogue;
    private string? _catalogueError;
    private bool _catalogueBusy;
    private readonly Dictionary<string, AddonProgress> _busy = new();

    /// <summary>The installed set or the catalogue changed: the page's list is stale.</summary>
    public event Action? Changed;
    /// <summary>An install moved on: downloading, installing, done or failed.</summary>
    public event Action<AddonProgress>? Progress;

    public AddonService(string themesDir, string extensionsDir, string recordsFile, string cacheDir,
        string themeBackups, string extensionBackups, Func<string?> indexUrl, Version launcher, HttpClient? http = null, Func<string?>? statsBase = null)
    {
        _statsBase = statsBase ?? (() => null);
        _themesDir = themesDir;
        _extensionsDir = extensionsDir;
        _recordsFile = recordsFile;
        _cacheDir = cacheDir;
        _themeBackups = themeBackups;
        _extBackups = extensionBackups;
        _indexUrl = indexUrl;
        _launcher = AddonVersion.From(launcher);
        _http = http ?? MakeHttp(launcher);
    }

    /// <summary>The real folders.</summary>
    public static AddonService ForApp(Func<string?> indexUrl, Func<string?> statsBase) => new(
        Paths.ThemesDir, Paths.ExtensionsDir, Paths.AddonsFile, Paths.AddonsCacheDir,
        Path.Combine(Paths.DataDir, "theme-backups"), Paths.ExtensionBackupsDir, indexUrl, UpdateService.Current,
        statsBase: statsBase);

    private static HttpClient MakeHttp(Version launcher)
    {
        var handler = new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Loungepad/{UpdateService.Format(launcher)} (+https://loungepad.app)");
        return http;
    }

    public static string KeyOf(string kind, string id) => $"{kind}:{id}";

    public string IndexUrl
    {
        get
        {
            var o = _indexUrl()?.Trim();
            return string.IsNullOrEmpty(o) ? DefaultIndexUrl : o;
        }
    }

    public string IconsDir => Path.Combine(_cacheDir, "icons");

    // ---- records ----

    public void Load()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(_recordsFile))
                    _records = JsonSerializer.Deserialize<AddonsFile>(File.ReadAllText(_recordsFile)) ?? new AddonsFile();
            }
            catch (Exception ex)
            {
                Log.Info($"Add-ons: addons.json could not be read, starting empty: {ex.Message}");
                _records = new AddonsFile();
            }
            try
            {
                var cache = Path.Combine(_cacheDir, "index.json");
                if (File.Exists(cache))
                    _catalogue = JsonSerializer.Deserialize<CatalogueCache>(File.ReadAllText(cache), ManifestOpts);
            }
            catch (Exception ex)
            {
                Log.Info($"Add-ons: the cached index could not be read: {ex.Message}");
                _catalogue = null;
            }
        }
    }

    private void SaveRecords()
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_recordsFile)!);
                File.WriteAllText(_recordsFile, JsonSerializer.Serialize(_records, FileOpts));
            }
            catch (Exception ex) { Log.Info($"Add-ons: could not save addons.json: {ex.Message}"); }
        }
    }

    public AddonRecord? Record(string key)
    {
        lock (_gate) return _records.Installed.TryGetValue(key, out var r) ? r : null;
    }

    public void RecordPass(string key, PassSummary summary)
    {
        lock (_gate)
        {
            if (!_records.Installed.TryGetValue(key, out var r)) return;
            r.LastPass = summary;
        }
        SaveRecords();
        Changed?.Invoke();
    }

    // ---- what is installed ----

    /// <summary>Every theme and extension on disk, themes first, each in name order.</summary>
    public List<InstalledAddon> Installed()
    {
        var list = new List<InstalledAddon>();
        list.AddRange(ScanFolder(_themesDir, AddonKind.Theme));
        list.AddRange(ScanFolder(_extensionsDir, AddonKind.Extension));
        return list;
    }

    public InstalledAddon? Find(string key) => Installed().FirstOrDefault(a => a.Key == key);

    private IEnumerable<InstalledAddon> ScanFolder(string root, string kind)
    {
        if (!Directory.Exists(root)) yield break;
        string[] dirs;
        try { dirs = Directory.GetDirectories(root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToArray(); }
        catch (Exception ex) { Log.Info($"Add-ons: could not list {root}: {ex.Message}"); yield break; }
        foreach (var dir in dirs)
        {
            var id = Path.GetFileName(dir);
            if (!IsFolderId(id)) continue;
            var (manifest, error) = ReadManifest(dir, id, kind);
            AddonRecord? record;
            lock (_gate) _records.Installed.TryGetValue(KeyOf(kind, id), out record);
            yield return new InstalledAddon
            {
                Id = id, Kind = kind, Manifest = manifest, Folder = dir, Record = record, Error = error,
                Bundled = kind == AddonKind.Theme && File.Exists(Path.Combine(dir, ".shipped")),
            };
        }
    }

    /// <summary>A folder name the lists accept: ThemeService's rule, so a theme dropped in by hand
    /// under any name it already allowed is still listed here.</summary>
    private static bool IsFolderId(string id) =>
        id.Length is > 0 and <= 64 && !id.StartsWith('.') && !id.Contains("..")
        && !id.Any(c => c is '/' or '\\' or '?' or '#' or ':');

    /// <summary>The manifest of a folder: theme.json for a theme (optional), manifest.json for an
    /// extension (required). Never throws; a bad file is an error on the result.</summary>
    private static (AddonManifest Manifest, string? Error) ReadManifest(string dir, string folderId, string kind)
    {
        var file = Path.Combine(dir, kind == AddonKind.Theme ? "theme.json" : "manifest.json");
        AddonManifest? m = null;
        string? error = null;
        if (File.Exists(file))
        {
            try { m = JsonSerializer.Deserialize<AddonManifest>(File.ReadAllText(file), ManifestOpts); }
            catch (Exception ex) { error = $"{Path.GetFileName(file)} is not valid: {ex.Message}"; }
        }
        else if (kind == AddonKind.Extension) error = "No manifest.json";
        m ??= new AddonManifest();
        try { Normalize(m, folderId, kind, strict: false); }
        catch (Exception ex) { error ??= ex.Message; }
        if (kind == AddonKind.Extension && error is null && !File.Exists(Path.Combine(dir, m.Main!)))
            error = $"The module {m.Main} is not in the folder";
        // An icon the manifest names but the folder lacks is no icon.
        if (m.Icon is not null && !File.Exists(Path.Combine(dir, m.Icon))) m.Icon = null;
        return (m, error);
    }

    /// <summary>
    /// Fill in the defaults and check what a manifest says. `strict` is an install: a wrong id,
    /// a bad host or a version that does not parse refuses it. Listing an installed folder is
    /// lenient, so that whatever is there can at least be seen and removed.
    /// </summary>
    private static void Normalize(AddonManifest m, string folderId, string kind, bool strict)
    {
        if (kind == AddonKind.Theme)
        {
            // theme.json names no id (the folder is the identity); one it does name is kept only
            // when it agrees with the folder.
            m.Id = folderId;
            m.Kind = AddonKind.Theme;
            m.Main = null;
        }
        else
        {
            m.Id = (m.Id ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(m.Id)) m.Id = folderId;
            if (!AddonManifest.IsValidId(m.Id)) throw new InvalidDataException($"The id \"{m.Id}\" is not valid: letters, digits and hyphens, starting with a letter");
            if (strict && m.Id != folderId) throw new InvalidDataException($"The manifest says \"{m.Id}\" but the folder is \"{folderId}\"");
            if (!string.Equals(m.Kind, AddonKind.Extension, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("manifest.json is not an extension's (kind must be \"extension\")");
            m.Kind = AddonKind.Extension;
            m.Main = string.IsNullOrWhiteSpace(m.Main) ? "main.js" : m.Main.Trim();
            if (!SafeRelativePath(m.Main)) throw new InvalidDataException($"The module path \"{m.Main}\" is not allowed");
        }
        m.Name = string.IsNullOrWhiteSpace(m.Name) ? m.Id : m.Name.Trim();
        m.Version = (m.Version ?? "").Trim();
        if (strict && kind == AddonKind.Extension && !AddonVersion.TryParse(m.Version, out _))
            throw new InvalidDataException($"The version \"{m.Version}\" is not major.minor.patch");
        m.Description = Trimmed(m.Description, 600);
        m.Author = Trimmed(m.Author, 80);
        m.Homepage = SafeHttpUrl(m.Homepage);
        if (m.Icon is not null && !IsIconPath(m.Icon)) { if (strict) throw new InvalidDataException($"The icon \"{m.Icon}\" is not an svg, png, jpg or webp in the folder"); m.Icon = null; }
        m.MinLauncher = Trimmed(m.MinLauncher, 20);
        if (m.Permissions?.Hosts is { } hosts)
        {
            var clean = new List<string>();
            foreach (var h in hosts)
            {
                var host = (h ?? "").Trim().ToLowerInvariant();
                if (!HostPattern.IsMatch(host)) { if (strict) throw new InvalidDataException($"The host \"{h}\" is not a host name"); continue; }
                if (!clean.Contains(host)) clean.Add(host);
            }
            m.Permissions.Hosts = clean;
        }
        if (m.Contributes?.Metadata is { } md)
        {
            md.StaleAfterDays = Math.Clamp(md.StaleAfterDays, 1, 365);
            md.RetryAfterDays = Math.Clamp(md.RetryAfterDays, 1, 365);
            md.PaceMs = Math.Clamp(md.PaceMs, 100, 60000);
        }
        if (m.Contributes?.GameFacts is { } facts)
        {
            m.Contributes.GameFacts = facts
                .Where(f => f is not null && Regex.IsMatch(f.Key ?? "", "^[A-Za-z][A-Za-z0-9_]{0,31}$"))
                .Select(f => new GameFact { Key = f.Key, Label = Trimmed(f.Label, 40) ?? f.Key, Format = (f.Format ?? "text").ToLowerInvariant() })
                .Take(12).ToList();
        }
    }

    private static string? Trimmed(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        return s.Length <= max ? s : s[..max];
    }

    private static string? SafeHttpUrl(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return Uri.TryCreate(s.Trim(), UriKind.Absolute, out var u) && u.Scheme is "https" or "http" && s.Length <= 300 ? u.ToString() : null;
    }

    private static bool IsIconPath(string p) => SafeRelativePath(p) && !p.Contains('/') && IconExtensions.Contains(Path.GetExtension(p));

    /// <summary>A path a manifest or the index may name: relative, forward slashes, plain
    /// segments, no dot-files (the launcher writes those itself), no climbing out.</summary>
    public static bool SafeRelativePath(string? p)
    {
        if (string.IsNullOrEmpty(p) || p.Length > 200) return false;
        if (p.Contains('\\') || p.Contains(':') || p.StartsWith('/') || p.EndsWith('/')) return false;
        foreach (var seg in p.Split('/'))
        {
            if (seg.Length == 0 || seg.StartsWith('.') || seg.EndsWith('.') || seg.EndsWith(' ')) return false;
            if (seg.Any(c => c < ' ' || c is '<' or '>' or '"' or '|' or '?' or '*')) return false;
        }
        return true;
    }

    /// <summary>True when the launcher is older than the add-on asks for.</summary>
    public bool NeedsNewerLauncher(string? minLauncher) =>
        AddonVersion.TryParse(minLauncher, out var min) && min.CompareTo(_launcher) > 0;

    // ---- the catalogue ----

    public IReadOnlyList<CatalogueEntry> Available
    {
        get { lock (_gate) return _catalogue?.Index.Addons.ToList() ?? new List<CatalogueEntry>(); }
    }

    public CatalogueEntry? CatalogueEntry(string kind, string id) =>
        Available.FirstOrDefault(e => e.Kind == kind && e.Id == id);

    public object CatalogueStatus()
    {
        lock (_gate)
            return new
            {
                url = IndexUrl, fetchedAt = _catalogue?.FetchedAt, error = _catalogueError,
                count = _catalogue?.Index.Addons.Count ?? 0, busy = _catalogueBusy,
                // A cache from another URL is still shown; the page says where it came from.
                stale = _catalogue is null || _catalogue.Url != IndexUrl || DateTime.UtcNow - _catalogue.FetchedAt > CatalogueFreshness,
            };
    }

    /// <summary>Read the index again. Unforced, a fresh cache is left alone. Returns whether it changed.</summary>
    public async Task<bool> RefreshCatalogueAsync(bool force, CancellationToken ct = default)
    {
        var url = IndexUrl;
        lock (_gate)
        {
            if (_catalogueBusy) return false;
            if (!force && _catalogue is not null && _catalogue.Url == url && DateTime.UtcNow - _catalogue.FetchedAt < CatalogueFreshness) return false;
            _catalogueBusy = true;
        }
        try
        {
            var bytes = await DownloadAsync(url, MaxIndexBytes, ct);
            var index = JsonSerializer.Deserialize<CatalogueIndex>(bytes, ManifestOpts) ?? throw new InvalidDataException("the index is empty");
            var clean = new CatalogueIndex { Schema = index.Schema, Generated = index.Generated, Addons = new() };
            var seen = new HashSet<string>();
            foreach (var e in index.Addons ?? new())
            {
                var problem = CheckEntry(e, url);
                if (problem is not null) { Log.Info($"Add-ons: index entry \"{e?.Id}\" skipped: {problem}"); continue; }
                if (!seen.Add(KeyOf(e!.Kind, e.Id))) continue;
                clean.Addons.Add(e);
            }
            var cache = new CatalogueCache { Url = url, FetchedAt = DateTime.UtcNow, Index = clean };
            lock (_gate) { _catalogue = cache; _catalogueError = null; }
            try
            {
                Directory.CreateDirectory(_cacheDir);
                File.WriteAllText(Path.Combine(_cacheDir, "index.json"), JsonSerializer.Serialize(cache, FileOpts));
            }
            catch (Exception ex) { Log.Info($"Add-ons: could not cache the index: {ex.Message}"); }
            Log.Info($"Add-ons: index read from {url}, {clean.Addons.Count} add-on(s)");
            await CacheCatalogueIconsAsync(clean, ct);
            await RefreshCountsAsync(force: true, ct);
            return true;
        }
        catch (Exception ex)
        {
            var msg = ex is HttpRequestException or TaskCanceledException ? "Could not reach the add-ons repository" : $"The index could not be read: {ex.Message}";
            lock (_gate) _catalogueError = msg;
            Log.Info($"Add-ons: refreshing the index from {url} failed: {ex.Message}");
            return false;
        }
        finally
        {
            lock (_gate) _catalogueBusy = false;
            Changed?.Invoke();
        }
    }

    /// <summary>Why an index entry is not usable, or null. Checked here so nothing downstream has
    /// to: by the time an entry is installed every path and hash in it has been looked at.</summary>
    private static string? CheckEntry(CatalogueEntry? e, string indexUrl)
    {
        if (e is null) return "empty";
        e.Id = (e.Id ?? "").Trim().ToLowerInvariant();
        e.Kind = (e.Kind ?? "").Trim().ToLowerInvariant();
        if (!AddonManifest.IsValidId(e.Id)) return "bad id";
        if (!AddonKind.IsValid(e.Kind)) return "bad kind";
        if (!AddonVersion.TryParse(e.Version, out _)) return "bad version";
        e.Name = string.IsNullOrWhiteSpace(e.Name) ? e.Id : e.Name.Trim();
        e.Summary = Trimmed(e.Summary, 300);
        e.Author = Trimmed(e.Author, 80);
        e.Homepage = SafeHttpUrl(e.Homepage);
        e.MinLauncher = Trimmed(e.MinLauncher, 20);
        if (!Uri.TryCreate(e.Base, UriKind.Absolute, out var baseUri) || !e.Base.EndsWith('/')) return "bad base URL";
        // https, or plain http only to the loopback the index itself came from: a dev index served locally.
        var loopback = baseUri.IsLoopback && Uri.TryCreate(indexUrl, UriKind.Absolute, out var iu) && iu.IsLoopback;
        if (baseUri.Scheme != "https" && !loopback) return "base URL is not https";
        if (e.Files is null || e.Files.Count == 0 || e.Files.Count > MaxFiles) return "no files, or too many";
        long total = 0;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in e.Files)
        {
            if (f is null || !SafeRelativePath(f.Path) || !paths.Add(f.Path)) return $"bad file path \"{f?.Path}\"";
            f.Sha256 = (f.Sha256 ?? "").Trim().ToLowerInvariant();
            if (!Sha256Pattern.IsMatch(f.Sha256)) return $"bad hash for {f.Path}";
            if (f.Size < 0 || f.Size > MaxFileBytes) return $"bad size for {f.Path}";
            total += f.Size;
        }
        if (total > MaxAddonBytes) return "too large";
        if (e.Icon is not null && !IsIconPath(e.Icon)) e.Icon = null;
        if (e.Permissions?.Hosts is { } hosts)
            e.Permissions.Hosts = hosts.Select(h => (h ?? "").Trim().ToLowerInvariant()).Where(h => HostPattern.IsMatch(h)).Distinct().ToList();
        var required = e.Kind == AddonKind.Theme ? "theme.css" : "manifest.json";
        if (!e.Files.Any(f => f.Path.Equals(required, StringComparison.OrdinalIgnoreCase))) return $"no {required}";
        return null;
    }

    /// <summary>The icons of what the index lists, kept beside it so the Available tiles have a
    /// picture without a network request each. Named by version, so a new version's icon is fetched
    /// and the old file falls away.</summary>
    private async Task CacheCatalogueIconsAsync(CatalogueIndex index, CancellationToken ct)
    {
        foreach (var e in index.Addons)
        {
            if (e.Icon is null) continue;
            var file = Path.Combine(IconsDir, CatalogueIconName(e));
            if (File.Exists(file)) continue;
            try
            {
                var bytes = await DownloadAsync(e.Base + e.Icon, MaxIconBytes, ct);
                Directory.CreateDirectory(IconsDir);
                File.WriteAllBytes(file, bytes);
                foreach (var old in Directory.GetFiles(IconsDir, $"cat-{e.Kind}-{e.Id}-*"))
                    if (!old.Equals(file, StringComparison.OrdinalIgnoreCase)) TryDelete(old);
            }
            catch (Exception ex) { Log.Info($"Add-ons: icon for {e.Id} not cached: {ex.Message}"); }
        }
    }

    private static string CatalogueIconName(CatalogueEntry e) => $"cat-{e.Kind}-{e.Id}-{e.Version}{Path.GetExtension(e.Icon!).ToLowerInvariant()}";

    /// <summary>The page's URL for a cached icon file, stamped with its mtime so a new icon shows.</summary>
    private static string? IconUrl(string? file)
    {
        if (file is null || !File.Exists(file)) return null;
        return $"https://loungepad.data/addons/icons/{Uri.EscapeDataString(Path.GetFileName(file))}?v={new FileInfo(file).LastWriteTimeUtc.Ticks}";
    }

    /// <summary>An installed add-on's icon, copied out of its folder into the icon cache (the
    /// extension folder itself is never served to the page) and refreshed when the source is newer.</summary>
    private string? InstalledIconFile(InstalledAddon a)
    {
        var icon = a.Manifest.Icon;
        if (icon is null) return null;
        var src = Path.Combine(a.Folder, icon);
        if (!File.Exists(src)) return null;
        var dst = Path.Combine(IconsDir, $"{a.Kind}-{a.Id}{Path.GetExtension(icon).ToLowerInvariant()}");
        try
        {
            if (!File.Exists(dst) || File.GetLastWriteTimeUtc(dst) < File.GetLastWriteTimeUtc(src) || new FileInfo(src).Length != new FileInfo(dst).Length)
            {
                if (new FileInfo(src).Length > MaxIconBytes) return null;
                Directory.CreateDirectory(IconsDir);
                File.Copy(src, dst, overwrite: true);
            }
            return dst;
        }
        catch (Exception ex) { Log.Info($"Add-ons: icon for {a.Key} not cached: {ex.Message}"); return null; }
    }

    // ---- installing ----

    private bool BeginBusy(string key, string state)
    {
        lock (_gate)
        {
            if (_busy.ContainsKey(key)) return false;
            _busy[key] = new AddonProgress { Key = key, State = state };
            return true;
        }
    }

    private void Report(string key, string state, int percent = 0, string? error = null)
    {
        var p = new AddonProgress { Key = key, State = state, Percent = percent, Error = error };
        lock (_gate) { if (state is "done" or "failed") _busy.Remove(key); else _busy[key] = p; }
        Progress?.Invoke(p);
    }

    /// <summary>Install, or update, what the repository lists under this key.</summary>
    public async Task<InstalledAddon> InstallFromCatalogueAsync(string key, CancellationToken ct = default)
    {
        var entry = Available.FirstOrDefault(e => KeyOf(e.Kind, e.Id) == key)
                    ?? throw new InvalidOperationException("That add-on is not in the repository's list");
        if (NeedsNewerLauncher(entry.MinLauncher))
            throw new InvalidOperationException($"Needs Loungepad {entry.MinLauncher}; this is {_launcher}");
        if (!BeginBusy(key, "downloading")) throw new InvalidOperationException("Already installing");
        var staging = NewStaging(entry.Id);
        try
        {
            Report(key, "downloading", 0);
            long total = Math.Max(1, entry.Files.Sum(f => f.Size)), done = 0;
            foreach (var f in entry.Files)
            {
                ct.ThrowIfCancellationRequested();
                var bytes = await DownloadAsync(entry.Base + f.Path, MaxFileBytes, ct);
                var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                if (hash != f.Sha256) throw new InvalidDataException($"{f.Path} does not match the checksum in the index");
                var dst = Path.Combine(staging, f.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                await File.WriteAllBytesAsync(dst, bytes, ct);
                done += bytes.Length;
                Report(key, "downloading", (int)Math.Min(99, done * 100 / total));
            }
            Report(key, "installing", 100);
            var manifest = ValidateStaged(staging, entry.Kind, entry.Id);
            var installed = Commit(staging, entry.Id, entry.Kind, manifest, new AddonRecord { Source = "catalogue" });
            Report(key, "done", 100);
            Log.Info($"Add-ons: installed {key} {manifest.Version} from the repository");
            // Counted only here: an install from the repository whose every file matched its hash.
            // An update counts too, once a day per address at most (the service decides).
            _ = ReportDownloadAsync(key);
            return installed;
        }
        catch (Exception ex)
        {
            Report(key, "failed", 0, ex.Message);
            Log.Info($"Add-ons: installing {key} failed: {ex.Message}");
            throw;
        }
        finally { TryDeleteDir(staging); }
    }

    /// <summary>A zip somebody downloaded: a theme or an extension, at the root or in one folder.</summary>
    public Task<InstalledAddon> InstallFromZipAsync(string zipPath) => Task.Run(() =>
    {
        var staging = NewStaging("zip");
        try
        {
            ExtractZip(zipPath, staging);
            var wrapper = Unwrap(staging);
            var fallback = SanitizeId(wrapper ?? Path.GetFileNameWithoutExtension(zipPath));
            var (kind, id, manifest) = DetectStaged(staging, fallback);
            var installed = Commit(staging, id, kind, manifest, new AddonRecord { Source = "zip", SourcePath = zipPath });
            Log.Info($"Add-ons: installed {installed.Key} {manifest.Version} from {zipPath}");
            return installed;
        }
        finally { TryDeleteDir(staging); }
    });

    /// <summary>A folder on this PC, copied in. The record keeps where it came from, so an edit
    /// there is one Reload away.</summary>
    public Task<InstalledAddon> InstallFromFolderAsync(string folder) => Task.Run(() =>
    {
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException($"{folder} is not a folder");
        var staging = NewStaging("folder");
        try
        {
            CopyDirectory(folder, staging, skipHidden: true);
            var (kind, id, manifest) = DetectStaged(staging, SanitizeId(Path.GetFileName(folder.TrimEnd('\\', '/'))));
            var target = TargetDir(kind, id);
            if (Path.GetFullPath(folder).TrimEnd('\\').Equals(Path.GetFullPath(target).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("That folder is where the add-on is already installed");
            var installed = Commit(staging, id, kind, manifest, new AddonRecord { Source = "folder", SourcePath = folder });
            Log.Info($"Add-ons: installed {installed.Key} {manifest.Version} from {folder}");
            return installed;
        }
        finally { TryDeleteDir(staging); }
    });

    /// <summary>Copy the folder or zip an add-on was installed from in again.</summary>
    public Task<InstalledAddon> ReloadFromSourceAsync(string key)
    {
        var r = Record(key);
        if (r?.SourcePath is null) throw new InvalidOperationException("This add-on was not installed from a folder or a zip");
        return r.Source == "zip" ? InstallFromZipAsync(r.SourcePath) : InstallFromFolderAsync(r.SourcePath);
    }

    private string NewStaging(string hint)
    {
        var dir = Path.Combine(_cacheDir, "staging", $"{SanitizeId(hint)}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private string TargetDir(string kind, string id) => Path.Combine(kind == AddonKind.Theme ? _themesDir : _extensionsDir, id);

    /// <summary>Which kind of add-on a staged folder is, and its id: manifest.json makes it an
    /// extension (the manifest names the id); theme.css or theme.json a theme, whose id is the
    /// manifest's if it gives one, else the folder's or the zip's name.</summary>
    private (string Kind, string Id, AddonManifest Manifest) DetectStaged(string staging, string fallbackId)
    {
        if (File.Exists(Path.Combine(staging, "manifest.json")))
        {
            var text = File.ReadAllText(Path.Combine(staging, "manifest.json"));
            AddonManifest m;
            try { m = JsonSerializer.Deserialize<AddonManifest>(text, ManifestOpts) ?? throw new InvalidDataException("empty"); }
            catch (JsonException ex) { throw new InvalidDataException($"manifest.json is not valid JSON: {ex.Message}"); }
            var id = (m.Id ?? "").Trim().ToLowerInvariant();
            if (!AddonManifest.IsValidId(id)) throw new InvalidDataException($"manifest.json's id \"{m.Id}\" is not valid");
            return (AddonKind.Extension, id, ValidateStaged(staging, AddonKind.Extension, id));
        }
        if (File.Exists(Path.Combine(staging, "theme.css")) || File.Exists(Path.Combine(staging, "theme.json")))
        {
            string? id = null;
            var file = Path.Combine(staging, "theme.json");
            if (File.Exists(file))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                    if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                    {
                        var declared = idEl.GetString()!.Trim().ToLowerInvariant();
                        if (AddonManifest.IsValidId(declared)) id = declared;
                    }
                }
                catch (JsonException ex) { throw new InvalidDataException($"theme.json is not valid JSON: {ex.Message}"); }
            }
            id ??= fallbackId;
            if (!AddonManifest.IsValidId(id)) throw new InvalidDataException("The theme needs a name: give the folder or the zip one made of letters, digits and hyphens");
            return (AddonKind.Theme, id, ValidateStaged(staging, AddonKind.Theme, id));
        }
        throw new InvalidDataException("Not a theme or an extension: no theme.css, theme.json or manifest.json inside");
    }

    /// <summary>Read and check a staged folder's manifest before anything is put in place.</summary>
    private AddonManifest ValidateStaged(string staging, string kind, string id)
    {
        var file = Path.Combine(staging, kind == AddonKind.Theme ? "theme.json" : "manifest.json");
        AddonManifest m = new();
        if (File.Exists(file))
        {
            try { m = JsonSerializer.Deserialize<AddonManifest>(File.ReadAllText(file), ManifestOpts) ?? new AddonManifest(); }
            catch (JsonException ex) { throw new InvalidDataException($"{Path.GetFileName(file)} is not valid JSON: {ex.Message}"); }
        }
        else if (kind == AddonKind.Extension) throw new InvalidDataException("No manifest.json");
        Normalize(m, id, kind, strict: true);
        if (kind == AddonKind.Theme && !File.Exists(Path.Combine(staging, "theme.css")) && !File.Exists(Path.Combine(staging, "theme.html")))
            throw new InvalidDataException("A theme needs a theme.css (or a theme.html)");
        if (kind == AddonKind.Extension && !File.Exists(Path.Combine(staging, m.Main!.Replace('/', Path.DirectorySeparatorChar))))
            throw new InvalidDataException($"The module {m.Main} is not in the folder");
        if (m.Icon is not null && !File.Exists(Path.Combine(staging, m.Icon))) m.Icon = null;
        if (NeedsNewerLauncher(m.MinLauncher)) throw new InvalidOperationException($"Needs Loungepad {m.MinLauncher}; this is {_launcher}");
        // Nothing the launcher writes itself may come in with an add-on.
        foreach (var stray in new[] { ".shipped" })
            TryDelete(Path.Combine(staging, stray));
        return m;
    }

    /// <summary>Put a validated staging folder in place: the old copy backed up first, the record
    /// written, the icon cached. Returns the installed add-on as the lists now see it.</summary>
    private InstalledAddon Commit(string staging, string id, string kind, AddonManifest manifest, AddonRecord record)
    {
        var target = TargetDir(kind, id);
        var key = KeyOf(kind, id);
        lock (_gate)
        {
            if (Directory.Exists(target))
            {
                if (kind == AddonKind.Theme && File.Exists(Path.Combine(target, ".shipped")))
                    throw new InvalidOperationException("That is a bundled theme, which the launcher keeps up to date itself");
                BackUp(target, kind, id);
                DeleteDirRetrying(target);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            CopyDirectory(staging, target, skipHidden: false);
            var old = _records.Installed.TryGetValue(key, out var r) ? r : null;
            record.Kind = kind;
            record.Version = manifest.Version;
            record.InstalledAt = DateTime.UtcNow;
            record.Enabled = old?.Enabled ?? true;
            record.LastPass = null;
            _records.Installed[key] = record;
        }
        SaveRecords();
        var installed = Find(key) ?? throw new InvalidOperationException("The add-on did not appear where it was installed");
        InstalledIconFile(installed);
        Changed?.Invoke();
        return installed;
    }

    /// <summary>Delete an installed add-on, keeping a copy in the backups folder. Returns what was
    /// removed, so the caller knows whether the theme in use just went.</summary>
    public InstalledAddon Remove(string key)
    {
        var a = Find(key) ?? throw new InvalidOperationException("That add-on is not installed");
        if (a.Bundled) throw new InvalidOperationException("That theme is part of Loungepad and cannot be removed");
        lock (_gate)
        {
            BackUp(a.Folder, a.Kind, a.Id);
            DeleteDirRetrying(a.Folder);
            _records.Installed.Remove(key);
        }
        SaveRecords();
        foreach (var f in Directory.Exists(IconsDir) ? Directory.GetFiles(IconsDir, $"{a.Kind}-{a.Id}.*") : Array.Empty<string>()) TryDelete(f);
        Log.Info($"Add-ons: removed {key}");
        Changed?.Invoke();
        return a;
    }

    /// <summary>Switch an extension on or off. A record is made for an extension that has none
    /// (a folder dropped in by hand), so the switch has somewhere to live.</summary>
    public void SetEnabled(string key, bool on)
    {
        var a = Find(key) ?? throw new InvalidOperationException("That add-on is not installed");
        lock (_gate)
        {
            if (!_records.Installed.TryGetValue(key, out var r))
                _records.Installed[key] = r = new AddonRecord { Kind = a.Kind, Version = a.Manifest.Version, Source = "manual", InstalledAt = DateTime.UtcNow };
            r.Enabled = on;
        }
        SaveRecords();
        Changed?.Invoke();
    }

    private void BackUp(string dir, string kind, string id)
    {
        try
        {
            var root = kind == AddonKind.Theme ? _themeBackups : _extBackups;
            Directory.CreateDirectory(root);
            var (m, _) = ReadManifest(dir, id, kind);
            var dst = Path.Combine(root, $"{id}-{(string.IsNullOrEmpty(m.Version) ? "unversioned" : m.Version)}-{DateTime.Now:yyyyMMdd-HHmmss}");
            CopyDirectory(dir, dst, skipHidden: false);
        }
        catch (Exception ex) { Log.Info($"Add-ons: could not back up {kind}:{id}: {ex.Message}"); }
    }

    // ---- the page's list ----

    /// <summary>Installed and available, one entry per add-on, in name order within each kind.
    /// `status` is asked for each installed extension: what the runtime says about it.</summary>
    public List<AddonDto> Describe(Func<InstalledAddon, object?>? status = null)
    {
        var list = new List<AddonDto>();
        var installed = Installed();
        var available = Available;
        Dictionary<string, AddonProgress> busy;
        lock (_gate) busy = new Dictionary<string, AddonProgress>(_busy);
        Dictionary<string, AddonCounts>? counts;
        HashSet<string> liked;
        lock (_gate) { counts = _counts; liked = new HashSet<string>(_records.Liked); }

        foreach (var a in installed)
        {
            var entry = available.FirstOrDefault(e => e.Kind == a.Kind && e.Id == a.Id);
            var m = a.Manifest;
            var dto = new AddonDto
            {
                Key = a.Key, Id = a.Id, Kind = a.Kind, Name = m.Name,
                Description = m.Description ?? entry?.Summary, Author = m.Author ?? entry?.Author, Homepage = m.Homepage ?? entry?.Homepage,
                Icon = IconUrl(InstalledIconFile(a)) ?? (entry?.Icon is null ? null : IconUrl(Path.Combine(IconsDir, CatalogueIconName(entry)))),
                Installed = true, Bundled = a.Bundled, Enabled = a.Enabled,
                Source = a.Bundled ? "bundled" : a.Record?.Source ?? "manual",
                CanReload = a.Record?.SourcePath is not null,
                Version = string.IsNullOrEmpty(m.Version) ? null : m.Version,
                Available = entry?.Version,
                Update = entry is not null && !a.Bundled && AddonVersion.IsNewer(entry.Version, m.Version),
                Installable = entry is not null && !NeedsNewerLauncher(entry.MinLauncher),
                NeedsLauncher = entry is not null && NeedsNewerLauncher(entry.MinLauncher) ? entry.MinLauncher : null,
                Hosts = m.Hosts, Contributes = m.Contributes, Settings = m.Settings,
                LastPass = a.Record?.LastPass, Error = a.Error,
                Status = a.Kind == AddonKind.Extension ? status?.Invoke(a) : null,
            };
            if (busy.TryGetValue(a.Key, out var p)) { dto.Busy = p.State; dto.Progress = p.Percent; }
            if (entry is not null) ApplyCounts(dto, counts, liked);
            list.Add(dto);
        }
        foreach (var e in available)
        {
            var key = KeyOf(e.Kind, e.Id);
            if (installed.Any(a => a.Key == key)) continue;
            var needs = NeedsNewerLauncher(e.MinLauncher);
            var dto = new AddonDto
            {
                Key = key, Id = e.Id, Kind = e.Kind, Name = e.Name, Description = e.Summary, Author = e.Author, Homepage = e.Homepage,
                Icon = e.Icon is null ? null : IconUrl(Path.Combine(IconsDir, CatalogueIconName(e))),
                Installed = false, Available = e.Version, Installable = !needs, NeedsLauncher = needs ? e.MinLauncher : null,
                Hosts = e.Permissions?.Hosts ?? new(),
            };
            if (busy.TryGetValue(key, out var p)) { dto.Busy = p.State; dto.Progress = p.Percent; }
            ApplyCounts(dto, counts, liked);
            list.Add(dto);
        }
        return list.OrderBy(d => d.Kind == AddonKind.Theme ? 0 : 1).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // ---- downloads and likes (addons-stats/) ----

    private static void ApplyCounts(AddonDto dto, Dictionary<string, AddonCounts>? counts, HashSet<string> liked)
    {
        dto.Liked = liked.Contains(dto.Key);
        if (counts is null) return;
        var c = counts.TryGetValue(dto.Key, out var found) ? found : new AddonCounts();
        dto.Downloads = c.Downloads;
        dto.Likes = c.Likes;
    }

    private string? StatsUrl(string path)
    {
        var b = _statsBase()?.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(b) || !Uri.TryCreate(b, UriKind.Absolute, out var u)) return null;
        if (u.Scheme != "https" && !u.IsLoopback) return null;
        return b + path;
    }

    private static HttpRequestMessage StatsRequest(HttpMethod method, string url, object? body = null)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.TryAddWithoutValidation(MetadataProxyClient.ClientHeader, MetadataProxyClient.ClientHeaderValue);
        if (body is not null)
            req.Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");
        return req;
    }

    /// <summary>Read every add-on's counts. Unforced, a copy younger than five minutes is kept.
    /// A failure keeps the last numbers: they are only ever decoration.</summary>
    public async Task<bool> RefreshCountsAsync(bool force, CancellationToken ct = default)
    {
        var url = StatsUrl("/v1/addons/stats");
        if (url is null) return false;
        lock (_gate)
        {
            if (_countsBusy || (!force && _counts is not null && DateTime.UtcNow - _countsAt < CountsFreshness)) return false;
            _countsBusy = true;
        }
        try
        {
            using var res = await _http.SendAsync(StatsRequest(HttpMethod.Get, url), ct);
            if (!res.IsSuccessStatusCode) { Log.Info($"Add-ons: counts answered HTTP {(int)res.StatusCode}"); return false; }
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            var map = new Dictionary<string, AddonCounts>();
            if (doc.RootElement.TryGetProperty("counts", out var c) && c.ValueKind == JsonValueKind.Object)
                foreach (var p in c.EnumerateObject())
                    map[p.Name] = new AddonCounts { Downloads = ReadCount(p.Value, "downloads"), Likes = ReadCount(p.Value, "likes") };
            lock (_gate) { _counts = map; _countsAt = DateTime.UtcNow; }
            Changed?.Invoke();
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Log.Info($"Add-ons: counts not read: {ex.Message}");
            return false;
        }
        finally { lock (_gate) _countsBusy = false; }
    }

    private static long ReadCount(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? Math.Max(0, n) : 0;

    /// <summary>Take the service's answer to a write as the add-on's counts from now on.</summary>
    private void TakeCounts(string key, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var c = new AddonCounts { Downloads = ReadCount(doc.RootElement, "downloads"), Likes = ReadCount(doc.RootElement, "likes") };
        lock (_gate) { (_counts ??= new())[key] = c; }
    }

    private async Task ReportDownloadAsync(string key)
    {
        var url = StatsUrl("/v1/addons/download");
        if (url is null) return;
        try
        {
            using var res = await _http.SendAsync(StatsRequest(HttpMethod.Post, url, new { key }));
            if (res.IsSuccessStatusCode) { TakeCounts(key, await res.Content.ReadAsStringAsync()); Changed?.Invoke(); }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Log.Info($"Add-ons: download of {key} not counted: {ex.Message}");
        }
    }

    /// <summary>
    /// Like an add-on, or take the like back. This PC's answer is kept in addons.json first, so the
    /// heart is right whatever the service says; the service counts at most one like and one unlike
    /// per address per add-on per day, and answers with the counts after. Only add-ons the
    /// repository lists can be liked.
    /// </summary>
    public async Task SetLikedAsync(string key, bool on)
    {
        var colon = key.IndexOf(':');
        if (colon < 0 || CatalogueEntry(key[..colon], key[(colon + 1)..]) is null)
            throw new InvalidOperationException("Only add-ons from the repository can be liked");
        bool changed;
        lock (_gate) changed = on ? _records.Liked.Add(key) : _records.Liked.Remove(key);
        if (!changed) return;
        // Shown at once: the count moves with the heart, and the service's answer replaces it.
        lock (_gate)
            if (_counts is not null)
            {
                if (!_counts.TryGetValue(key, out var c)) _counts[key] = c = new AddonCounts();
                c.Likes = Math.Max(0, c.Likes + (on ? 1 : -1));
            }
        SaveRecords();
        Changed?.Invoke();
        var url = StatsUrl("/v1/addons/like");
        if (url is null) return;
        try
        {
            using var res = await _http.SendAsync(StatsRequest(HttpMethod.Post, url, new { key, on }));
            if (res.IsSuccessStatusCode) TakeCounts(key, await res.Content.ReadAsStringAsync());
            else Log.Info($"Add-ons: like of {key} answered HTTP {(int)res.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Log.Info($"Add-ons: like of {key} not sent: {ex.Message}");
        }
        Changed?.Invoke();
    }

    // ---- files ----

    private async Task<byte[]> DownloadAsync(string url, long maxBytes, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)response.StatusCode} for {url}");
        if (response.Content.Headers.ContentLength is { } len && len > maxBytes) throw new InvalidDataException($"{url} is too large ({len} bytes)");
        await using var src = await response.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await src.ReadAsync(buffer, ct)) > 0)
        {
            ms.Write(buffer, 0, read);
            if (ms.Length > maxBytes) throw new InvalidDataException($"{url} is too large");
        }
        return ms.ToArray();
    }

    /// <summary>Unpack a zip into the staging folder with every entry's path checked: nothing
    /// climbs out, nothing is absolute, and the size and count stay under the caps.</summary>
    private static void ExtractZip(string zipPath, string staging)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var root = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
        long total = 0;
        var count = 0;
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.EndsWith('/') || name.Length == 0) continue;              // a folder entry
            if (name.StartsWith("__MACOSX/") || name.Split('/').Any(s => s == ".DS_Store")) continue;
            if (!SafeRelativePath(name)) throw new InvalidDataException($"The zip holds a path that is not allowed: {entry.FullName}");
            if (++count > MaxFiles) throw new InvalidDataException("The zip holds too many files for an add-on");
            if (entry.Length > MaxFileBytes || (total += entry.Length) > MaxAddonBytes) throw new InvalidDataException("The zip is too large for an add-on");
            var dst = Path.GetFullPath(Path.Combine(staging, name.Replace('/', Path.DirectorySeparatorChar)));
            if (!dst.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"The zip holds a path that is not allowed: {entry.FullName}");
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            entry.ExtractToFile(dst, overwrite: true);
        }
        if (count == 0) throw new InvalidDataException("The zip is empty");
    }

    /// <summary>A zip made of one folder (what GitHub's "Download ZIP" gives) is unwrapped so the
    /// manifest sits at the root. Returns the folder's name, which is the theme's id when the
    /// theme names none.</summary>
    private static string? Unwrap(string staging)
    {
        var dirs = Directory.GetDirectories(staging);
        var files = Directory.GetFiles(staging);
        if (dirs.Length != 1 || files.Length != 0) return null;
        var inner = dirs[0];
        var name = Path.GetFileName(inner);
        var temp = Path.Combine(Path.GetDirectoryName(staging)!, Path.GetFileName(staging) + "-inner");
        Directory.Move(inner, temp);
        foreach (var d in Directory.GetDirectories(temp)) Directory.Move(d, Path.Combine(staging, Path.GetFileName(d)));
        foreach (var f in Directory.GetFiles(temp)) File.Move(f, Path.Combine(staging, Path.GetFileName(f)));
        TryDeleteDir(temp);
        return name;
    }

    /// <summary>A folder or file name as an add-on id: lower case, anything else a hyphen.</summary>
    public static string SanitizeId(string? name)
    {
        var s = Regex.Replace((name ?? "").Trim().ToLowerInvariant(), "[^a-z0-9-]+", "-").Trim('-');
        s = Regex.Replace(s, "-+", "-");
        if (s.Length > 0 && !char.IsLetter(s[0])) s = "a-" + s;
        return s.Length > 40 ? s[..40].TrimEnd('-') : s;
    }

    private static void CopyDirectory(string src, string dst, bool skipHidden)
    {
        Directory.CreateDirectory(dst);
        foreach (var dir in Directory.GetDirectories(src))
        {
            var name = Path.GetFileName(dir);
            if (skipHidden && (name.StartsWith('.') || name is "node_modules")) continue;
            CopyDirectory(dir, Path.Combine(dst, name), skipHidden);
        }
        foreach (var file in Directory.GetFiles(src))
        {
            var name = Path.GetFileName(file);
            if (skipHidden && name.StartsWith('.')) continue;
            File.Copy(file, Path.Combine(dst, name), overwrite: true);
        }
    }

    private static void DeleteDirRetrying(string dir)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { Directory.Delete(dir, recursive: true); return; }
            catch (IOException) when (attempt < 5) { Thread.Sleep(150); }
            catch (UnauthorizedAccessException) when (attempt < 5) { Thread.Sleep(150); }
        }
    }

    private static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* staging; the next start sweeps it */ }
    }

    private static void TryDelete(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); } catch { /* cosmetic */ }
    }

    /// <summary>Staging folders left by a crash mid-install.</summary>
    public void Sweep()
    {
        try
        {
            var staging = Path.Combine(_cacheDir, "staging");
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
        catch { /* next time */ }
    }
}

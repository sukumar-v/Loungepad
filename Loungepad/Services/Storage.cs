using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Loungepad.Models;

namespace Loungepad.Services;

public static class Paths
{
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Loungepad");

    /// <summary>Caches that should not roam: the WebView2 profiles and downloaded updates.</summary>
    public static string LocalDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Loungepad");

    public static string CoversDir { get; } = Path.Combine(DataDir, "covers");
    /// <summary>One folder per theme. Reachable from the page as https://loungepad.data/themes/…
    /// because DataDir is mapped as a virtual host, so a theme's own files need no further
    /// plumbing to load.</summary>
    public static string ThemesDir { get; } = Path.Combine(DataDir, "themes");
    /// <summary>One folder per installed extension (Settings → Add-ons; see docs/ADDONS.md). The
    /// folder is what says an extension is installed; addons.json beside it only says where it
    /// came from and whether it is enabled.</summary>
    public static string ExtensionsDir { get; } = Path.Combine(DataDir, "extensions");
    public static string AddonsFile { get; } = Path.Combine(DataDir, "addons.json");
    /// <summary>Each extension's own small JSON store (loungepad.storage in its API).</summary>
    public static string ExtensionDataDir { get; } = Path.Combine(DataDir, "extension-data");
    /// <summary>A copy of each extension replaced or removed, as theme-backups is for themes.</summary>
    public static string ExtensionBackupsDir { get; } = Path.Combine(DataDir, "extension-backups");

    /// <summary>
    /// WebView2's profile. Deliberately NOT under DataDir: WebView2 refuses to serve a virtual
    /// host mapping for a folder that contains its own user data folder, and DataDir is mapped
    /// as loungepad.data. With the profile inside it, every request to that host failed -- cover
    /// art included -- which looked like missing art rather than a broken mapping.
    ///
    /// It is a rebuildable cache, so living somewhere else costs nothing; Local is where a cache
    /// belongs anyway, and it keeps it out of a roaming profile.
    /// </summary>
    public static string WebViewDir { get; } = Path.Combine(LocalDir, "webview2");
    /// <summary>Downloaded trailers, a few hundred megabytes each. Under Local like the other
    /// caches -- gigabytes of video have no business in a roaming profile -- and reached from the
    /// page as https://loungepad.data/trailers/…, which MainWindow routes here by hand.</summary>
    public static string TrailersDir { get; } = Path.Combine(LocalDir, "trailers");
    /// <summary>Achievement icons, a few kilobytes each and a few thousand of them: a cache, under
    /// Local like the trailers, reached from the page as https://loungepad.data/achievements/…</summary>
    public static string AchievementIconsDir { get; } = Path.Combine(LocalDir, "achievements");
    /// <summary>The add-ons repository's index as last fetched, and the icons of what it lists and
    /// of what is installed, reached from the page as https://loungepad.data/addons/icons/…</summary>
    public static string AddonsCacheDir { get; } = Path.Combine(LocalDir, "addons");
    public static string SettingsFile { get; } = Path.Combine(DataDir, "settings.json");
    public static string LibraryFile { get; } = Path.Combine(DataDir, "library.json");
    /// <summary>The last list of games the Steam account owned, so a start with no network keeps
    /// the uninstalled half of the library rather than losing it until the next fetch.</summary>
    public static string SteamOwnedFile { get; } = Path.Combine(DataDir, "steam-owned.json");
    /// <summary>The PC Game Pass catalogue as last fetched; it is a few dozen requests to
    /// rebuild and changes a handful of times a month.</summary>
    public static string GamePassFile { get; } = Path.Combine(DataDir, "gamepass.json");
    public static string LogFile { get; } = Path.Combine(DataDir, "loungepad.log");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(CoversDir);
        Directory.CreateDirectory(ThemesDir);
    }
}

public static class Log
{
    private static readonly object Gate = new();

    /// <summary>
    /// Roll over at a megabyte, keeping one previous file.
    ///
    /// This only ever appended, so on a launcher that starts with Windows and runs all day the
    /// file grew without limit -- slowly, which is the kind that goes unnoticed until it is
    /// hundreds of megabytes. One megabyte is several thousand lines, far more than anything
    /// worth reading back, and keeping the previous file means a problem from before the roll is
    /// still there to look at.
    /// </summary>
    private const long MaxBytes = 1024 * 1024;

    public static void Info(string message)
    {
        try
        {
            lock (Gate)
            {
                Roll();
                File.AppendAllText(Paths.LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never take the app down */ }
    }

    private static void Roll()
    {
        try
        {
            var info = new FileInfo(Paths.LogFile);
            if (!info.Exists || info.Length < MaxBytes) return;
            File.Move(Paths.LogFile, Paths.LogFile + ".old", overwrite: true);
        }
        catch { /* a locked or missing file is not worth losing the line over */ }
    }
}

/// <summary>
/// Writing a store so that a process ended halfway leaves the old file whole. File.WriteAllText
/// empties the file first and writes after, and an exit (closing for an update, a crash, a kill)
/// ends whatever background save is running -- the metadata pass's checkpoint, the achievements
/// pass -- leaving a truncated file the next start cannot read.
/// </summary>
public static class SafeFile
{
    /// <summary>Written beside the file, flushed to disk, then renamed over it. The rename is
    /// atomic on NTFS, so readers see the old file or the new one and never part of either.</summary>
    public static void WriteAllText(string path, string text)
    {
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(new System.Text.UTF8Encoding(false).GetBytes(text));
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>A copy that is either whole or not there.</summary>
    public static void Copy(string from, string to)
    {
        var tmp = to + ".tmp";
        File.Copy(from, tmp, overwrite: true);
        File.Move(tmp, to, overwrite: true);
    }

    /// <summary>Moves an unreadable file out of the way under a name nothing else writes,
    /// <c>name.unreadable-yyyyMMdd-HHmmss</c>, and never over an earlier one. The new name, or
    /// null when it could not be moved.</summary>
    public static string? SetAside(string path)
    {
        try
        {
            var aside = $"{path}.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}";
            for (int n = 2; File.Exists(aside); n++) aside = $"{path}.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}-{n}";
            File.Move(path, aside);
            return aside;
        }
        catch { return null; }
    }
}

public class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public AppSettings Settings { get; private set; } = new();

    /// <summary>"#RRGGBB", and nothing else.</summary>
    public static bool IsHexColor(string? value) =>
        value is not null && value.Length == 7 && value[0] == '#' && value.Skip(1).All(Uri.IsHexDigit);

    /// <summary>
    /// Up to 1.11 one "menu combo" (MinimizeCombo) did both jobs, its gesture (MenuComboMode)
    /// deciding which: tap-and-hold was a tap for the Power Wheel and a hold for Loungepad, double
    /// tap the other way round. They are two settings now; a file from before carries the old pair
    /// and not the new, and becomes the same two jobs on the same button.
    /// </summary>
    internal static void MigrateMenuCombo(System.Text.Json.Nodes.JsonObject o, AppSettings s)
    {
        if (o.ContainsKey(nameof(AppSettings.WheelCombo)) || o["MinimizeCombo"] is not { } old) return;
        string combo = old.GetValueKind() == JsonValueKind.String ? old.GetValue<string>() : "Guide";
        bool doubleTap = o["MenuComboMode"] is { } m && m.GetValueKind() == JsonValueKind.String
            && string.Equals(m.GetValue<string>(), "DoubleTap", StringComparison.OrdinalIgnoreCase);
        s.WheelCombo = s.LauncherCombo = string.IsNullOrWhiteSpace(combo) ? "Off" : combo;
        s.WheelGesture = doubleTap ? "DoubleTap" : "Tap";
        s.LauncherGesture = doubleTap ? "Tap" : "Hold";
        s.LauncherComboAction = "Toggle";
        Log.Info($"Settings: the menu combo ({combo}, {(doubleTap ? "double tap" : "tap and hold")}) is now the Power Wheel and Loungepad buttons");
    }

    public void Load()
    {
        try
        {
            if (File.Exists(Paths.SettingsFile))
            {
                var text = File.ReadAllText(Paths.SettingsFile);
                Settings = JsonSerializer.Deserialize<AppSettings>(text) ?? new AppSettings();
                // Written before the first-run setup existed: this install was set up by hand, so it
                // counts as having been through the first version of the setup. Only a file with
                // no settings at all is a new install.
                if (System.Text.Json.Nodes.JsonNode.Parse(text) is System.Text.Json.Nodes.JsonObject o)
                {
                    if (!o.ContainsKey(nameof(AppSettings.OnboardingVersion)))
                        Settings.OnboardingVersion = 1;
                    MigrateMenuCombo(o, Settings);
                }
            }

            // The accent reaches the UI as a CSS value, so a hand-edited file must not be able to
            // put anything but a hex colour there. Checked on the way in as well as on save,
            // because a file edited by hand never passes through the save path at all.
            if (!IsHexColor(Settings.AccentColor)) Settings.AccentColor = new AppSettings().AccentColor;

            // A bundled theme that has been renamed keeps the user on it. Without this the id in
            // settings.json matches nothing, and someone who chose a theme is silently moved back
            // to the built-in look for no reason they can see.
            if (Settings.Theme is { Length: > 0 } theme
                && ThemeService.Renamed.TryGetValue(theme, out var renamed))
            {
                Settings.Theme = renamed;
                Log.Info($"Theme '{theme}' is now '{renamed}'");
            }
        }
        catch (Exception ex)
        {
            // Kept, not saved over: the defaults are written on the next change, and the file
            // that would not load is still there to recover by hand.
            var aside = ex is JsonException && File.Exists(Paths.SettingsFile) ? SafeFile.SetAside(Paths.SettingsFile) : null;
            Log.Info($"Settings load failed, using defaults: {ex.Message}" + (aside is null ? "" : $"; kept as {Path.GetFileName(aside)}"));
            Settings = new AppSettings();
        }
    }

    public void Save()
    {
        Paths.EnsureCreated();
        SafeFile.WriteAllText(Paths.SettingsFile, JsonSerializer.Serialize(Settings, JsonOpts));
    }
}

public class CollectionDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> GameIds { get; set; } = new();
}

public class LibraryFileData
{
    public List<Game> Games { get; set; } = new();
    public List<CollectionDef> Collections { get; set; } = new();
    /// <summary>Emulators and ROM folders live here rather than in settings.json: they describe
    /// the library, like collections do, and "Restore default settings" promises to leave the
    /// library alone.</summary>
    public List<EmulatorDef> Emulators { get; set; } = new();
    public List<RomFolderDef> RomFolders { get; set; } = new();
    /// <summary>What was removed by hand, so detection does not put it straight back. Paths,
    /// because that is what detection finds things by.</summary>
    public List<string> IgnoredEmulatorPaths { get; set; } = new();
    public List<string> IgnoredRomFolderPaths { get; set; } = new();
}

public class LibraryStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private readonly object _gate = new();
    private readonly string _file;

    /// <summary>The file is an argument so a harness can load and save a scratch library (the
    /// Playnite import's apply and undo) without touching the real one.</summary>
    public LibraryStore(string? file = null)
    {
        _file = file ?? Paths.LibraryFile;
    }

    public List<Game> Games { get; private set; } = new();
    public List<CollectionDef> Collections { get; private set; } = new();
    public List<EmulatorDef> Emulators { get; private set; } = new();
    public List<RomFolderDef> RomFolders { get; private set; } = new();
    public List<string> IgnoredEmulatorPaths { get; private set; } = new();
    public List<string> IgnoredRomFolderPaths { get; private set; } = new();

    /// <summary>Why nothing may be written over library.json this session, or null. Set when the
    /// file was there and could not be read: it may be perfectly good, and a save would replace
    /// it with whatever this session managed to load.</summary>
    private string? _saveBlocked;
    private bool _saveBlockedLogged;

    /// <summary>
    /// A file that cannot be read is never written over (a user's report, Nov 2026: hand-added
    /// games gone after an update). It used to start empty, and the scan that follows every start
    /// saved the store games it found over the top -- everything a scan cannot find again (manual
    /// games, playtime, collections, emulators) gone for good. The file was unreadable because a
    /// save was cut off: Save truncated it first and wrote after, and closing for an update ends
    /// the process with a metadata checkpoint still writing (a harness killing a saving process
    /// left it unreadable 8 times in 30). Now: a locked file is retried, then the last good copy
    /// is shown and nothing saved; a broken file is kept beside it and the last good copy loaded.
    /// </summary>
    public void Load()
    {
        _saveBlocked = null;
        _saveBlockedLogged = false;
        LibraryFileData? data = null;
        if (File.Exists(_file))
        {
            string? text = null;
            // Something else may have it open as we start (a sync client, a virus scanner).
            for (int attempt = 1; text is null; attempt++)
            {
                try { text = File.ReadAllText(_file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (attempt < 10) { Thread.Sleep(200); continue; }
                    _saveBlocked = $"library.json could not be read ({ex.Message})";
                    Log.Info($"Library: {_saveBlocked}. Nothing will be saved over it this session");
                    break;
                }
            }
            if (text is not null)
            {
                data = Parse(text, out var why);
                if (data is null)
                {
                    var aside = SafeFile.SetAside(_file);
                    if (aside is null) _saveBlocked = $"library.json is unreadable ({why}) and could not be moved aside";
                    Log.Info(aside is null
                        ? $"Library: {_saveBlocked}. Nothing will be saved over it this session"
                        : $"Library: library.json is unreadable ({why}); kept as {Path.GetFileName(aside)}");
                }
                else
                {
                    // The last copy that loaded, for the start that finds this one broken.
                    try { SafeFile.Copy(_file, BackupFile); }
                    catch (Exception ex) { Log.Info($"Library: could not keep a backup copy: {ex.Message}"); }
                }
            }
            if (data is null && File.Exists(BackupFile))
            {
                try
                {
                    data = Parse(File.ReadAllText(BackupFile), out var why);
                    Log.Info(data is null
                        ? $"Library: the backup copy is unreadable too ({why})"
                        : $"Library: restored {data.Games.Count} games from the copy kept on {File.GetLastWriteTime(BackupFile):yyyy-MM-dd HH:mm}");
                }
                catch (Exception ex) { Log.Info($"Library: the backup copy could not be read: {ex.Message}"); }
            }
            if (data is null) Log.Info("Library: starting empty");
        }
        data ??= new LibraryFileData();
        Games = data.Games ?? new();
        Collections = data.Collections ?? new();
        Emulators = data.Emulators ?? new();
        RomFolders = data.RomFolders ?? new();
        IgnoredEmulatorPaths = data.IgnoredEmulatorPaths ?? new();
        IgnoredRomFolderPaths = data.IgnoredRomFolderPaths ?? new();

        // A critic score is Metacritic's or nothing. Older builds filled the gaps with IGDB's own
        // average, and those go here rather than showing until the metadata pass reaches each
        // game -- which at store pace is half an hour into a big library.
        foreach (var g in Games)
        {
            if (g.CriticSource == "Metacritic") continue;
            g.CriticScore = null;
            g.CriticSource = null;
        }
    }

    private string BackupFile => _file + ".bak";

    /// <summary>The file's contents, or null with the reason when they are not a library.</summary>
    private static LibraryFileData? Parse(string text, out string why)
    {
        why = "";
        try
        {
            // pre-collections format: a bare game array
            if (text.TrimStart().StartsWith('['))
                return JsonSerializer.Deserialize<List<Game>>(text) is { } games ? new LibraryFileData { Games = games } : Null(out why);
            return JsonSerializer.Deserialize<LibraryFileData>(text) ?? Null(out why);
        }
        catch (Exception ex)
        {
            why = ex.Message;
            return null;
        }

        static LibraryFileData? Null(out string why) { why = "it holds null"; return null; }
    }

    public void Save()
    {
        lock (_gate)
        {
            if (_saveBlocked is not null)
            {
                if (!_saveBlockedLogged) Log.Info($"Library: not saved, {_saveBlocked}");
                _saveBlockedLogged = true;
                return;
            }
            Paths.EnsureCreated();
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var data = new LibraryFileData
            {
                Games = Games, Collections = Collections, Emulators = Emulators, RomFolders = RomFolders,
                IgnoredEmulatorPaths = IgnoredEmulatorPaths, IgnoredRomFolderPaths = IgnoredRomFolderPaths,
            };
            SafeFile.WriteAllText(_file, JsonSerializer.Serialize(data, JsonOpts));
        }
    }

    /// <summary>Merge freshly scanned games, preserving locally tracked playtime/session data.</summary>
    public void MergeScanned(IEnumerable<Game> scanned)
    {
        lock (_gate)
        {
            var manual = Games.Where(g => g.Manual).ToList();
            var byId = Games.ToDictionary(g => g.Id);
            var merged = new List<Game>();

            foreach (var s in scanned)
            {
                if (byId.TryGetValue(s.Id, out var old))
                {
                    s.PlaytimeMinutes = old.PlaytimeMinutes;
                    s.Sessions = old.Sessions;
                    s.Favorite = old.Favorite;
                    s.Hidden = old.Hidden;
                    s.PreferredEdition = old.PreferredEdition;
                    if (old.LastPlayed is not null && (s.LastPlayed is null || old.LastPlayed > s.LastPlayed))
                        s.LastPlayed = old.LastPlayed;
                    // Art: the scan only ever finds Steam's local half-size cache, so anything
                    // MetadataService already upgraded has to survive a rescan or every scan
                    // would undo it and the next enrich would download it all again.
                    s.CoverFile = KeepBest(s.CoverFile, old.CoverFile);
                    s.BannerFile = KeepBest(s.BannerFile, old.BannerFile);
                    s.HeroFile = KeepBest(s.HeroFile, old.HeroFile);
                    s.LogoFile = KeepBest(s.LogoFile, old.LogoFile);
                    // No KeepBest: a scan never finds a backdrop, so there is nothing to weigh it
                    // against. It was missing from this list entirely, which meant every scan
                    // dropped it and the next enrich had to fetch it again.
                    s.BackdropFile = old.BackdropFile;
                    // Nor a square, which only a theme's request ever fetches.
                    s.SquareFile = old.SquareFile;
                    s.SquareCheckedAt = old.SquareCheckedAt;

                    // Fetched metadata is not discoverable from disk at all. EVERY fetched field
                    // has to be listed here -- the same trap as CopySettings, and it fails the
                    // same silent way: PegiRating was missing, so a rating would have been wiped
                    // by the next scan and the feature would have looked broken with nothing in
                    // the code to point at.
                    s.Description = old.Description;
                    s.Developer = old.Developer;
                    s.Publisher = old.Publisher;
                    s.Genres = old.Genres;
                    s.ReleaseDate = old.ReleaseDate;
                    s.CriticScore = old.CriticScore;
                    s.CriticSource = old.CriticSource;
                    s.PegiRating = old.PegiRating;
                    s.EsrbRating = old.EsrbRating;
                    s.EsrbDescriptors = old.EsrbDescriptors;
                    s.PegiDescriptors = old.PegiDescriptors;
                    s.ControllerSupport = old.ControllerSupport;
                    s.TrailerUrl = old.TrailerUrl;
                    s.TrailerFile = old.TrailerFile;
                    s.Media = old.Media;
                    s.MetadataSource = old.MetadataSource;
                    s.MetadataFetched = old.MetadataFetched;
                    // Without this the stamp resets to 0 on every scan, every game looks like it
                    // was filled in by an older build, and the whole library is re-fetched on
                    // every single start.
                    s.MetadataVersion = old.MetadataVersion;
                    // What the extensions stored (docs/ADDONS.md): fetched, so not on disk to
                    // find, and the same silent loss as every field above if left out.
                    s.Ext = old.Ext;
                    // Just installed. The lite pass an uninstalled Steam game gets (see
                    // MetadataService) stops at the cover and the tile; clearing the stamp is
                    // what fetches the hero, the backdrop and the wordmark now that there is a
                    // detail page worth dressing. Placed after the copy above, or the copy would
                    // put the old stamp straight back.
                    if (s.Installed && !old.Installed) s.MetadataFetched = null;
                    // user overrides survive rescans
                    if (!string.IsNullOrWhiteSpace(old.Args)) s.Args = old.Args;
                    if (old.PreferDirectLaunch) { s.PreferDirectLaunch = true; s.ExePath = old.ExePath; }
                    // A ROM's title is guessed from its file name and a rescan guesses the same
                    // thing again, so one typed in by hand has to be carried across or "Rename"
                    // would undo itself on the next start. The per-game emulator is an override
                    // in the same sense as Args.
                    if (old.TitleEdited) { s.Title = old.Title; s.TitleEdited = true; }
                    if (old.EmulatorId is not null) s.EmulatorId = old.EmulatorId;
                    // Store ids the scan cannot see: the manifest scan knows Epic's namespace but
                    // not Xbox's title id, and an owned-games answer knows both; whichever this
                    // scan lacked is kept from last time, or the achievements would go on every scan.
                    s.XboxTitleId ??= old.XboxTitleId;
                    s.EpicNamespace ??= old.EpicNamespace;
                    s.UbisoftSpaceId ??= old.UbisoftSpaceId;
                    s.EaOfferId ??= old.EaOfferId;
                    // Fields a newer build wrote that this one cannot read (see Game.Unknown). A
                    // rescan builds every entry afresh, so without this they would go on the
                    // first scan instead of the first save.
                    s.Unknown = old.Unknown;
                }
                merged.Add(s);
            }

            merged.AddRange(manual);
            Games = merged;
        }
        Save();
    }

    // ---- Emulators and ROM folders ----

    public EmulatorDef? FindEmulator(string? id) =>
        id is null ? null : Emulators.FirstOrDefault(e => e.Id == id);

    public RomFolderDef? FindRomFolder(string? id) =>
        id is null ? null : RomFolders.FirstOrDefault(f => f.Id == id);

    /// <summary>The emulator this ROM starts with: its own choice if it has one, else its folder's.</summary>
    public EmulatorDef? EmulatorFor(Game game) =>
        FindEmulator(game.EmulatorId) ?? FindEmulator(FindRomFolder(game.RomFolderId)?.EmulatorId);

    // Adding by hand takes the path off the ignore list, and removing puts it on: "I removed it"
    // means "do not find it again", however it got there, and "I added it back" means the reverse.

    public void AddEmulator(EmulatorDef emulator)
    {
        lock (_gate)
        {
            Emulators.Add(emulator);
            IgnoredEmulatorPaths.RemoveAll(p => p.Equals(emulator.ExePath, StringComparison.OrdinalIgnoreCase));
        }
        Save();
    }

    /// <summary>Removes the emulator and un-assigns it from every folder and game that named it,
    /// so nothing is left pointing at an id that no longer exists.</summary>
    public void RemoveEmulator(string id)
    {
        lock (_gate)
        {
            foreach (var e in Emulators.Where(e => e.Id == id))
                if (!IgnoredEmulatorPaths.Contains(e.ExePath, StringComparer.OrdinalIgnoreCase))
                    IgnoredEmulatorPaths.Add(e.ExePath);
            Emulators.RemoveAll(e => e.Id == id);
            foreach (var f in RomFolders) if (f.EmulatorId == id) f.EmulatorId = null;
            foreach (var g in Games) if (g.EmulatorId == id) g.EmulatorId = null;
        }
        Save();
    }

    public void AddRomFolder(RomFolderDef folder)
    {
        lock (_gate)
        {
            RomFolders.Add(folder);
            IgnoredRomFolderPaths.RemoveAll(p => p.Equals(folder.Path, StringComparison.OrdinalIgnoreCase));
        }
        Save();
    }

    /// <summary>The folder's games go with it on the next scan, which only keeps what was scanned.</summary>
    public void RemoveRomFolder(string id)
    {
        lock (_gate)
        {
            foreach (var f in RomFolders.Where(f => f.Id == id))
                if (!IgnoredRomFolderPaths.Contains(f.Path, StringComparer.OrdinalIgnoreCase))
                    IgnoredRomFolderPaths.Add(f.Path);
            RomFolders.RemoveAll(f => f.Id == id);
        }
        Save();
    }

    /// <summary>
    /// Picks between the art a fresh scan found and the art already on record. Downloaded art wins
    /// over anything the scan found locally -- it is the full-resolution version of the same
    /// picture, or a shape the local cache does not hold at all -- and a user's hand-picked cover
    /// wins over both, which is what the "custom_" prefix marks.
    /// </summary>
    /// <summary>
    /// Art a scan may replace, and art it may not.
    ///
    /// The scan only ever finds Steam's local cache, which is half size and whatever shape the
    /// client happened to store -- so anything MetadataService downloaded or the user chose has
    /// to outlast a rescan, or every start would throw the good art away and the library would
    /// visibly change between sessions.
    ///
    /// The markers below ARE the naming scheme, so they have to move with it. When the fetched
    /// names changed from "_hd*" to "_st_*"/"_sv_*" this test went on matching the old one, which
    /// meant it stopped recognising fetched art at all and every scan quietly reverted the whole
    /// library to the local cache. "_hd" stays in the list for installs that have not re-fetched
    /// yet; it costs nothing and its absence would cost them their art for one pass.
    /// </summary>
    private static readonly string[] FetchedMarkers = { "_st_", "_sv_", "_pf_", "_hd" };

    private static string? KeepBest(string? scanned, string? existing)
    {
        if (existing is null) return scanned;
        if (scanned is null) return existing;
        var kept = existing.StartsWith("custom_", StringComparison.Ordinal)
                   || FetchedMarkers.Any(m => existing.Contains(m, StringComparison.Ordinal));
        return kept ? existing : scanned;
    }

    public Game? Find(string id)
    {
        lock (_gate) return Games.FirstOrDefault(g => g.Id == id);
    }

    /// <summary>Runs a change to the game or collection lists under the store's lock: the Playnite
    /// import adds and removes games and collections in bulk.</summary>
    public void Change(Action change)
    {
        lock (_gate) change();
    }

    public void AddManual(Game game)
    {
        lock (_gate) Games.Add(game);
        Save();
    }

    public void Remove(string id)
    {
        lock (_gate)
        {
            Games.RemoveAll(g => g.Id == id);
            foreach (var c in Collections) c.GameIds.Remove(id);
        }
        Save();
    }

    public string CreateCollection(string name)
    {
        var col = new CollectionDef { Id = Guid.NewGuid().ToString("N"), Name = name };
        lock (_gate) Collections.Add(col);
        Save();
        return col.Id;
    }

    public void DeleteCollection(string id)
    {
        lock (_gate) Collections.RemoveAll(c => c.Id == id);
        Save();
    }

    /// <summary>Returns true if the game is now in the collection.</summary>
    public bool ToggleInCollection(string collectionId, string gameId)
    {
        bool added = false;
        lock (_gate)
        {
            var col = Collections.FirstOrDefault(c => c.Id == collectionId);
            if (col is null) return false;
            if (!col.GameIds.Remove(gameId)) { col.GameIds.Add(gameId); added = true; }
        }
        Save();
        return added;
    }
}

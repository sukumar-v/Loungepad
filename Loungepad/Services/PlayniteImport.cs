using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using LiteDB;
using Loungepad.Models;
using Microsoft.Win32;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace Loungepad.Services;

/// <summary>
/// Bringing a Playnite library across: what a person built up there, laid into the gaps in what
/// Loungepad has. Three steps, and nothing is written until the last:
///
///   Read   Playnite's database (LiteDB, one file per collection under library\), its settings,
///          and the two extensions this app's Stats replace -- GameActivity's sessions and
///          SuccessStory's lists -- under ExtensionsData. Playnite's files are never opened in
///          place: the database files are copied to a temp folder and read there, because
///          LiteDB 5 reads Playnite's v4 files only by upgrading them, and because Playnite may
///          be running and holding them.
///   Plan   Every change the import would make, against Loungepad's data as it stands now. The
///          page shows the plan as counts per part and imports only the parts left on.
///   Apply  FILLS, NEVER OVERWRITES (the user's rule, Sept 2026): anything Loungepad already has
///          is left exactly as it is, even where Playnite's is bigger. A playtime, a play count or
///          a last-played date lands only where Loungepad's is empty; a favourite or hidden mark
///          only on a game without one; a category becomes a new collection, never an addition
///          to one of the user's (a clash of names gets " (Playnite)"); a session only where
///          Loungepad recorded nothing overlapping it; a SuccessStory list only for a game
///          Loungepad has no list for and cannot fetch one for; a setting only where it is empty.
///          Sessions, hand-added games and lists carry the Playnite original's key, so a second
///          import brings nothing the first already did.
///
/// Every change is written to playnite-import.json as it is made, and Undo walks that record
/// back -- leaving alone anything that has changed since, and taking playtime back by the amount
/// the import added rather than to a stored value, so an evening played after the import stays.
/// </summary>
public static class PlayniteImport
{
    // Playnite's own library plugins, by the ids they ship under (the folder names under
    // ExtensionsData). Anything else is matched by title.
    private static readonly Guid SteamPlugin = Guid.Parse("cb91dfc9-b977-43bf-8e70-55f46e410fab");
    private static readonly Guid GogPlugin = Guid.Parse("aebe8b7c-6dc3-4a66-af31-e7375c6b5e9e");
    private static readonly Guid EpicPlugin = Guid.Parse("00000002-dbd1-46c6-b5d0-b1ba559d10e4");
    private static readonly Guid XboxPlugin = Guid.Parse("7e4fbb5e-2ae3-48d4-8ba0-6b30e7a4e287");

    /// <summary>The Source of a list brought in from SuccessStory, and the Origin of a session
    /// brought in from GameActivity.</summary>
    public const string Origin = "playnite";

    // ---- what is read ----

    public sealed record PnAction(string Type, string? Path, string? Arguments, string? WorkingDir, bool IsPlayAction, bool Emulated);

    public sealed class PnGame
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = "";
        public string? GameId { get; init; }
        public Guid PluginId { get; init; }
        public long PlaytimeSeconds { get; init; }
        public long PlayCount { get; init; }
        /// <summary>Local time.</summary>
        public DateTime? LastActivity { get; init; }
        public bool Favorite { get; init; }
        public bool Hidden { get; init; }
        public List<Guid> CategoryIds { get; init; } = new();
        public string? InstallDirectory { get; init; }
        public List<PnAction> Actions { get; init; } = new();
    }

    public sealed class PnSession
    {
        public Guid GameId { get; init; }
        /// <summary>Local time.</summary>
        public DateTime Start { get; init; }
        public int Seconds { get; init; }
        public List<ActivitySample> Samples { get; init; } = new();
    }

    public sealed class PnAchievements
    {
        public Guid GameId { get; init; }
        public List<Achievement> Items { get; init; } = new();
    }

    public sealed class PnData
    {
        public string Dir { get; init; } = "";
        public string? Version { get; init; }
        public List<PnGame> Games { get; init; } = new();
        public Dictionary<Guid, string> Categories { get; init; } = new();
        public string? SteamGridDbKey { get; init; }
        public bool HasGameActivity { get; init; }
        public bool HasSuccessStory { get; init; }
        public List<PnSession> Sessions { get; init; } = new();
        public List<PnAchievements> Achievements { get; init; } = new();
    }

    // ---- where Playnite keeps it ----

    /// <summary>The Playnite data folder on this PC: the installed build's %APPDATA%\Playnite, or a
    /// portable build's own folder, found through its uninstall entry. Null when neither has a
    /// library.</summary>
    public static string? FindDataDir()
    {
        var roaming = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Playnite");
        if (LibraryDir(roaming) is not null) return roaming;
        var loc = UninstallValue("InstallLocation");
        if (loc is not null && LibraryDir(loc) is not null) return loc.TrimEnd('\\');
        return null;
    }

    /// <summary>Accepts the data folder, its library folder, or a portable install's folder.</summary>
    public static string? NormaliseDataDir(string picked)
    {
        var trimmed = picked.TrimEnd('\\');
        if (LibraryDir(trimmed) is not null) return trimmed;
        var parent = Path.GetDirectoryName(trimmed);
        if (Path.GetFileName(trimmed).Equals("library", StringComparison.OrdinalIgnoreCase)
            && parent is not null && LibraryDir(parent) is not null) return parent;
        return null;
    }

    /// <summary>The folder with games.db: config.json's DatabasePath when it names one, else
    /// library\ beside it.</summary>
    public static string? LibraryDir(string dataDir)
    {
        try
        {
            var cfg = Path.Combine(dataDir, "config.json");
            if (ReadJson(cfg)?["DatabasePath"] is JsonValue v && v.TryGetValue<string>(out var custom) && custom.Length > 0)
            {
                var expanded = Environment.ExpandEnvironmentVariables(custom.Replace("{PlayniteDir}", dataDir, StringComparison.OrdinalIgnoreCase));
                if (!Path.IsPathRooted(expanded)) expanded = Path.Combine(dataDir, expanded);
                if (File.Exists(Path.Combine(expanded, "games.db"))) return expanded;
            }
        }
        catch { /* fall through to the default */ }
        var lib = Path.Combine(dataDir, "library");
        return File.Exists(Path.Combine(lib, "games.db")) ? lib : null;
    }

    private static string? UninstallValue(string name)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Playnite_is1");
            return k?.GetValue(name) as string;
        }
        catch { return null; }
    }

    // ---- reading ----

    public static PnData Read(string dataDir)
    {
        var libDir = LibraryDir(dataDir) ?? throw new InvalidOperationException("Playnite's library was not found there");
        var temp = Path.Combine(Path.GetTempPath(), "Loungepad-playnite-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);
        try
        {
            var games = ReadCollection(libDir, temp, "games.db").Select(ToGame).Where(g => g.Name.Length > 0 && g.Id != Guid.Empty).ToList();
            var categories = ReadCollection(libDir, temp, "categories.db")
                .Where(d => d["_id"].IsGuid && d["Name"].IsString && d["Name"].AsString.Trim().Length > 0)
                .GroupBy(d => d["_id"].AsGuid)
                .ToDictionary(g => g.Key, g => g.First()["Name"].AsString.Trim());

            var ext = Path.Combine(dataDir, "ExtensionsData");
            var activityDirs = ExtensionDataDirs(ext, "GameActivity");
            var successDirs = ExtensionDataDirs(ext, "SuccessStory");
            return new PnData
            {
                Dir = dataDir,
                Version = UninstallValue("DisplayVersion"),
                Games = games,
                Categories = categories,
                SteamGridDbKey = FindSteamGridDbKey(ext),
                HasGameActivity = activityDirs.Count > 0,
                HasSuccessStory = successDirs.Count > 0,
                Sessions = activityDirs.SelectMany(SafeFiles).SelectMany(ReadGameActivity).ToList(),
                Achievements = successDirs.SelectMany(SafeFiles).Select(ReadSuccessStory).Where(a => a is { Items.Count: > 0 }).Select(a => a!).ToList(),
            };
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* the temp folder is swept eventually */ }
        }
    }

    /// <summary>Every document of the one collection in a Playnite .db file, read from a copy.
    /// A missing file is empty rather than an error: a library with no categories may have no
    /// categories.db worth the name.</summary>
    private static List<BsonDocument> ReadCollection(string libDir, string temp, string file)
    {
        var src = Path.Combine(libDir, file);
        if (!File.Exists(src)) return new List<BsonDocument>();
        var copy = Path.Combine(temp, file);
        // FileShare.ReadWrite: Playnite may have the file open while it runs.
        using (var from = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var to = File.Create(copy))
            from.CopyTo(to);
        using var db = new LiteDatabase(new ConnectionString { Filename = copy, Upgrade = true, Connection = ConnectionType.Direct });
        var name = db.GetCollectionNames().FirstOrDefault();
        return name is null ? new List<BsonDocument>() : db.GetCollection(name).FindAll().ToList();
    }

    private static PnGame ToGame(BsonDocument d)
    {
        static string? Str(BsonValue v) => v.IsString ? v.AsString : null;
        static long Num(BsonValue v) => v.IsNumber ? v.AsInt64 : 0;
        var actions = new List<PnAction>();
        if (d["GameActions"].IsArray)
            foreach (var a in d["GameActions"].AsArray.Where(x => x.IsDocument).Select(x => x.AsDocument))
                actions.Add(new PnAction(
                    Str(a["Type"]) ?? (a["Type"].IsNumber ? a["Type"].AsInt32.ToString(CultureInfo.InvariantCulture) : ""),
                    Str(a["Path"]), Str(a["Arguments"]), Str(a["WorkingDir"]),
                    a["IsPlayAction"].IsBoolean && a["IsPlayAction"].AsBoolean,
                    a["EmulatorId"].IsGuid && a["EmulatorId"].AsGuid != Guid.Empty));
        return new PnGame
        {
            Id = d["_id"].IsGuid ? d["_id"].AsGuid : Guid.Empty,
            Name = Str(d["Name"])?.Trim() ?? "",
            GameId = Str(d["GameId"]),
            PluginId = d["PluginId"].IsGuid ? d["PluginId"].AsGuid : Guid.Empty,
            PlaytimeSeconds = Math.Max(0, Num(d["Playtime"])),
            PlayCount = Math.Max(0, Num(d["PlayCount"])),
            LastActivity = d["LastActivity"].IsDateTime ? AsLocal(d["LastActivity"].AsDateTime) : null,
            Favorite = d["Favorite"].IsBoolean && d["Favorite"].AsBoolean,
            Hidden = d["Hidden"].IsBoolean && d["Hidden"].AsBoolean,
            CategoryIds = d["CategoryIds"].IsArray ? d["CategoryIds"].AsArray.Where(x => x.IsGuid).Select(x => x.AsGuid).ToList() : new(),
            InstallDirectory = Str(d["InstallDirectory"]),
            Actions = actions,
        };
    }

    // LiteDB hands dates back in UTC.
    private static DateTime AsLocal(DateTime d) => d.Kind == DateTimeKind.Local ? d : DateTime.SpecifyKind(d, DateTimeKind.Utc).ToLocalTime();

    /// <summary>The extensions keep their data under ExtensionsData\&lt;plugin id&gt;\&lt;Name&gt;\,
    /// one JSON per game. Found by that folder name rather than by plugin id.</summary>
    private static List<string> ExtensionDataDirs(string ext, string name)
    {
        var found = new List<string>();
        try
        {
            if (!Directory.Exists(ext)) return found;
            foreach (var plugin in Directory.GetDirectories(ext))
            {
                var dir = Path.Combine(plugin, name);
                if (Directory.Exists(dir)) found.Add(dir);
            }
        }
        catch { /* unreadable ExtensionsData: nothing from the extensions */ }
        return found;
    }

    private static IEnumerable<string> SafeFiles(string dir)
    {
        try { return Directory.GetFiles(dir, "*.json"); } catch { return Array.Empty<string>(); }
    }

    private static JsonNode? ReadJson(string path)
    {
        try { return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path).TrimStart('﻿')) : null; }
        catch { return null; }
    }

    /// <summary>Extra Metadata Loader keeps a SteamGridDB key as SgdbApiKey; any extension's
    /// config that carries one will do. Playnite's own Steam key is encrypted in keys.dat and
    /// is not read.</summary>
    private static string? FindSteamGridDbKey(string ext)
    {
        try
        {
            if (!Directory.Exists(ext)) return null;
            foreach (var plugin in Directory.GetDirectories(ext))
                if (ReadJson(Path.Combine(plugin, "config.json")) is JsonObject cfg)
                    foreach (var name in new[] { "SgdbApiKey", "SteamGridDbApiKey", "SteamGridDBApiKey" })
                        if (Str(cfg[name]) is { } key && key.Trim().Length > 0)
                            return key.Trim();
        }
        catch { /* no key */ }
        return null;
    }

    /// <summary>
    /// One GameActivity file: { Id, Items: [{ DateSession, ElapsedSeconds }], ItemsDetails:
    /// { Items: { "&lt;DateSession&gt;": [{ Datelog, FPS, CPU, GPU, RAM, CPUT, GPUT, CPUP, GPUP }] } } }.
    /// DateSession is UTC. Read loosely -- a field that is missing or a different shape costs that
    /// field, never the file.
    /// </summary>
    public static List<PnSession> ReadGameActivity(string path)
    {
        var list = new List<PnSession>();
        try
        {
            if (ReadJson(path) is not JsonObject root) return list;
            var gameId = GuidOf(root["Id"]) ?? GuidOf(Path.GetFileNameWithoutExtension(path));
            if (gameId is null || root["Items"] is not JsonArray items) return list;
            var details = new List<(DateTime Key, JsonArray Logs)>();
            if (root["ItemsDetails"]?["Items"] is JsonObject byDate)
                foreach (var (k, v) in byDate)
                    if (v is JsonArray logs && ParseDate(k) is { } key) details.Add((key, logs));
            foreach (var item in items.OfType<JsonObject>())
            {
                if (ParseDate(Str(item["DateSession"])) is not { } start) continue;
                var seconds = (int)Math.Min(24 * 3600 * 7, Math.Max(0, Num(item["ElapsedSeconds"]) ?? 0));
                if (seconds < 30) continue;
                var samples = new List<ActivitySample>();
                var logs = details.FirstOrDefault(x => Math.Abs((x.Key - start).TotalSeconds) < 2).Logs;
                if (logs is not null)
                    foreach (var log in logs.OfType<JsonObject>())
                    {
                        var at = ParseDate(Str(log["Datelog"]));
                        samples.Add(new ActivitySample
                        {
                            T = at is { } t ? (int)Math.Clamp((t - start).TotalSeconds, 0, seconds) : samples.Count * 10,
                            Fps = IntOf(log["FPS"]), Cpu = IntOf(log["CPU"]), Gpu = IntOf(log["GPU"]), Ram = IntOf(log["RAM"]),
                            CpuTemp = IntOf(log["CPUT"]), GpuTemp = IntOf(log["GPUT"]),
                            CpuPower = IntOf(log["CPUP"]), GpuPower = IntOf(log["GPUP"]),
                        });
                    }
                list.Add(new PnSession { GameId = gameId.Value, Start = start, Seconds = seconds, Samples = samples });
            }
        }
        catch (Exception ex) { Log.Info($"Playnite import: {Path.GetFileName(path)} unreadable: {ex.Message}"); }
        return list;
    }

    /// <summary>
    /// One SuccessStory file: { Id, Items: [{ Name, ApiName, Description, UrlUnlocked, UrlLocked,
    /// DateUnlocked, Percent, IsHidden, GamerScore }] }. A locked one's DateUnlocked is null or
    /// 0001-01-01, and a Percent of 100 is SuccessStory's "unknown". Icons that are not web
    /// addresses (SuccessStory's own cache) are dropped rather than pointed at.
    /// </summary>
    public static PnAchievements? ReadSuccessStory(string path)
    {
        try
        {
            if (ReadJson(path) is not JsonObject root) return null;
            var gameId = GuidOf(root["Id"]) ?? GuidOf(Path.GetFileNameWithoutExtension(path));
            if (gameId is null || root["Items"] is not JsonArray items) return null;
            var list = new List<Achievement>();
            foreach (var item in items.OfType<JsonObject>())
            {
                var name = Str(item["Name"])?.Trim() ?? "";
                var api = Str(item["ApiName"])?.Trim();
                if (name.Length == 0 && string.IsNullOrEmpty(api)) continue;
                var at = ParseDate(Str(item["DateUnlocked"]));
                var pct = Num(item["Percent"]);
                var score = Num(item["GamerScore"]);
                list.Add(new Achievement
                {
                    Id = string.IsNullOrEmpty(api) ? name : api,
                    Name = name.Length > 0 ? name : api!,
                    Description = Str(item["Description"]),
                    Hidden = item["IsHidden"] is JsonValue h && h.TryGetValue<bool>(out var hidden) && hidden,
                    Unlocked = at is not null,
                    UnlockedAt = at,
                    Percent = pct is >= 0 and < 100 ? pct : null,
                    IconUrl = WebUrl(Str(item["UrlUnlocked"])),
                    IconLockedUrl = WebUrl(Str(item["UrlLocked"])),
                    Score = score is > 0 ? (int)Math.Round(score.Value) : null,
                });
            }
            return new PnAchievements { GameId = gameId.Value, Items = list };
        }
        catch (Exception ex) { Log.Info($"Playnite import: {Path.GetFileName(path)} unreadable: {ex.Message}"); return null; }
    }

    private static string? WebUrl(string? s) => s is not null && (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) ? s : null;
    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static double? Num(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
    private static int? IntOf(JsonNode? n) => Num(n) is { } d && d >= 0 && d < 100000 ? (int)Math.Round(d) : null;
    private static Guid? GuidOf(JsonNode? n) => GuidOf(Str(n));
    private static Guid? GuidOf(string? s) => s is not null && Guid.TryParse(s, out var g) && g != Guid.Empty ? g : null;

    /// <summary>A date as Playnite's serializer writes it: ISO with Z or an offset, or bare (read
    /// as UTC, which is what both extensions store). Local time out; null for "never".</summary>
    public static DateTime? ParseDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (!DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d)) return null;
        if (d.Year < 1990) return null;
        return DateTime.SpecifyKind(d, DateTimeKind.Utc).ToLocalTime();
    }

    // ---- matching ----

    /// <summary>
    /// Which Loungepad game each Playnite game is. By the store's own id where Playnite has one
    /// (Steam app id, GOG product id, Epic app name, Xbox package family name) -- exact, like the
    /// metadata; then a hand-added game imported earlier; then an exact title, only when exactly
    /// one game in the library has it. A game Loungepad does not have is left out.
    /// </summary>
    public static Dictionary<Guid, string> Match(PnData data, IReadOnlyList<Game> library)
    {
        var byId = library.GroupBy(g => g.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var map = new Dictionary<Guid, string>();
        foreach (var p in data.Games)
        {
            string? id = null;
            var gid = p.GameId;
            if (gid is { Length: > 0 })
            {
                if (p.PluginId == SteamPlugin) id = Have($"steam:{gid}");
                else if (p.PluginId == GogPlugin) id = Have($"gog:{gid}");
                else if (p.PluginId == EpicPlugin) id = Have($"epic:{gid}");
                else if (p.PluginId == XboxPlugin)
                    id = library.FirstOrDefault(g => g.PackageFamilyName is { } pfn && pfn.Equals(gid, StringComparison.OrdinalIgnoreCase))?.Id
                         ?? Have($"xbox:pfn:{gid}");
            }
            id ??= Have(ManualId(p.Id));
            if (id is null)
            {
                var hits = library.Where(g => !g.Emulated && TitleMatch.IsConfident(p.Name, g.Title)).Take(2).ToList();
                if (hits.Count == 1) id = hits[0].Id;
            }
            if (id is not null) map[p.Id] = id;
        }
        return map;

        string? Have(string key) => byId.TryGetValue(key, out var g) ? g.Id : null;
    }

    public static string ManualId(Guid playniteId) => $"manual:pn-{playniteId:N}";

    // ---- planning ----

    /// <summary>One game's totals as the import fills them. A field Loungepad already had keeps
    /// its value, so Before == After there.</summary>
    public sealed class PlaytimeChange
    {
        public string GameId { get; set; } = "";
        public double MinutesBefore { get; set; }
        public double MinutesAfter { get; set; }
        public int SessionsBefore { get; set; }
        public int SessionsAfter { get; set; }
        public DateTime? LastBefore { get; set; }
        public DateTime? LastAfter { get; set; }
    }

    public sealed class FlagChange
    {
        public string GameId { get; set; } = "";
        public bool Favorite { get; set; }
        public bool Hidden { get; set; }
    }

    /// <summary>A collection the import makes. Never one of the user's: on a clash of names the
    /// import's is called "Name (Playnite)". CollectionId is set once it exists.</summary>
    public sealed class CollectionChange
    {
        public string Name { get; set; } = "";
        public string? CollectionId { get; set; }
        public List<string> GameIds { get; set; } = new();
    }

    public sealed class SettingChange
    {
        public string Key { get; set; } = "";
        public string Label { get; set; } = "";
        public string? After { get; set; }
    }

    public sealed class Plan
    {
        public PnData Data { get; init; } = new();
        public int Matched { get; init; }
        public List<PlaytimeChange> Playtime { get; init; } = new();
        public List<FlagChange> Flags { get; init; } = new();
        public List<CollectionChange> Collections { get; init; } = new();
        public List<Game> Games { get; init; } = new();
        public List<(PlaySession Session, List<ActivitySample>? Samples)> Sessions { get; init; } = new();
        public List<GameAchievements> Achievements { get; init; } = new();
        public List<SettingChange> Settings { get; init; } = new();
    }

    /// <param name="canFetch">Whether Loungepad can fetch a game's achievements itself; a
    /// SuccessStory list is only brought for a game it cannot.</param>
    public static Plan MakePlan(PnData data, IReadOnlyList<Game> library, IReadOnlyList<CollectionDef> collections,
        IReadOnlyList<PlaySession> sessions, AchievementStore achievements, AppSettings settings, Func<Game, bool> canFetch,
        Record? previous = null)
    {
        var match = Match(data, library);
        var games = library.GroupBy(g => g.Id).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var pById = data.Games.GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First());
        var plan = new Plan { Data = data, Matched = match.Count };

        foreach (var (pid, gid) in match)
        {
            var p = pById[pid];
            var g = games[gid];
            // Each total only where Loungepad has none of its own. A bigger number from Playnite
            // does not replace a smaller one of ours: ours is what this launcher counted.
            var change = new PlaytimeChange
            {
                GameId = gid,
                MinutesBefore = g.PlaytimeMinutes, MinutesAfter = g.PlaytimeMinutes > 0 ? g.PlaytimeMinutes : p.PlaytimeSeconds / 60.0,
                SessionsBefore = g.Sessions, SessionsAfter = g.Sessions > 0 ? g.Sessions : (int)Math.Min(int.MaxValue, p.PlayCount),
                LastBefore = g.LastPlayed, LastAfter = g.LastPlayed ?? p.LastActivity,
            };
            if (change.MinutesAfter >= 1 && change.MinutesBefore <= 0 || change.SessionsAfter != change.SessionsBefore || change.LastAfter != change.LastBefore)
            {
                if (change.MinutesAfter < 1) change.MinutesAfter = change.MinutesBefore;
                plan.Playtime.Add(change);
            }
            var fav = p.Favorite && !g.Favorite;
            var hid = p.Hidden && !g.Hidden;
            if (fav || hid) plan.Flags.Add(new FlagChange { GameId = gid, Favorite = fav, Hidden = hid });
        }

        // Games added to Playnite by hand, pointing at a program on this PC, that Loungepad did not
        // find itself. Only a plain file action: an emulator profile, a URL or a script is
        // Playnite's own machinery and would not start the same way here.
        foreach (var p in data.Games.Where(p => p.PluginId == Guid.Empty && !match.ContainsKey(p.Id)))
        {
            var action = p.Actions.Where(a => !a.Emulated && a.Type is "File" or "0" && a.Path is { Length: > 0 })
                .OrderByDescending(a => a.IsPlayAction).FirstOrDefault();
            if (action is null) continue;
            var exe = Expand(action.Path!, p.InstallDirectory);
            if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(exe)) continue;
            var dir = action.WorkingDir is { Length: > 0 } w ? Expand(w, p.InstallDirectory) : Path.GetDirectoryName(exe);
            plan.Games.Add(new Game
            {
                Id = ManualId(p.Id), Title = p.Name, Platform = "Manual", Manual = true, TitleEdited = true,
                ExePath = exe, Args = string.IsNullOrWhiteSpace(action.Arguments) ? null : action.Arguments,
                InstallDir = dir, Installed = true, Favorite = p.Favorite, Hidden = p.Hidden,
                PlaytimeMinutes = p.PlaytimeSeconds / 60.0, Sessions = (int)Math.Min(int.MaxValue, p.PlayCount), LastPlayed = p.LastActivity,
            });
        }
        var added = plan.Games.Select(g => g.Id).ToHashSet();
        string? Target(Guid pid) => match.TryGetValue(pid, out var m) ? m : added.Contains(ManualId(pid)) ? ManualId(pid) : null;

        // Categories become collections of their own. A name the user already has gets
        // " (Playnite)"; one this import made on an earlier run is added to rather than repeated.
        var imported = previous?.Collections.Where(c => c.CollectionId is not null).Select(c => c.CollectionId!).ToHashSet() ?? new HashSet<string>();
        foreach (var (catId, name) in data.Categories)
        {
            var members = data.Games.Where(p => p.CategoryIds.Contains(catId)).Select(p => Target(p.Id)).OfType<string>().Distinct().ToList();
            if (members.Count == 0) continue;
            CollectionDef? Named(string n) => collections.FirstOrDefault(c => c.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
            var target = name;
            var existing = Named(target);
            if (existing is not null && !imported.Contains(existing.Id))
            {
                target = $"{name} (Playnite)";
                existing = Named(target);
                if (existing is not null && !imported.Contains(existing.Id)) continue;   // both names are the user's
            }
            var add = members.Where(id => existing is null || !existing.GameIds.Contains(id)).ToList();
            if (add.Count == 0) continue;
            plan.Collections.Add(new CollectionChange { Name = existing?.Name ?? target, CollectionId = existing?.Id, GameIds = add });
        }

        // GameActivity's sessions, where Loungepad recorded nothing overlapping for that game.
        // They never go into the totals, which come across on their own above.
        var haveKeys = sessions.Where(s => s.ImportKey is not null).Select(s => s.ImportKey!).ToHashSet();
        var ours = sessions.Where(s => s.Origin is null).GroupBy(s => s.GameId).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var s in data.Sessions)
        {
            if (Target(s.GameId) is not { } gid) continue;
            var key = $"{s.GameId:N}@{s.Start.ToUniversalTime():yyyyMMddHHmmss}";
            if (!haveKeys.Add(key)) continue;
            var end = s.Start.AddSeconds(s.Seconds);
            if (ours.TryGetValue(gid, out var mine) && mine.Any(m => m.Start < end && m.End > s.Start)) continue;
            plan.Sessions.Add((new PlaySession
            {
                Id = ActivityStore.NewId(), GameId = gid, Start = s.Start, End = end, Seconds = s.Seconds,
                Origin = Origin, ImportKey = key, Sources = s.Samples.Count > 0 ? "GameActivity" : null,
            }, s.Samples.Count > 0 ? s.Samples : null));
        }

        // SuccessStory's lists, for games Loungepad has no list for and cannot fetch one for:
        // a list of ours -- the store's answer -- is never edited by the import.
        foreach (var a in data.Achievements)
        {
            if (Target(a.GameId) is not { } gid || achievements.Get(gid) is not null) continue;
            if (games.TryGetValue(gid, out var g) && canFetch(g)) continue;
            plan.Achievements.Add(new GameAchievements
            {
                GameId = gid, Source = Origin, SourceName = pById.TryGetValue(a.GameId, out var pg) ? pg.Name : null,
                FetchedAt = DateTime.UtcNow, Items = a.Items,
            });
        }

        // Settings with a home here, and only where ours is empty. Playnite's "start with
        // Windows" is not brought: Loungepad's switch has a value already, even when it is off.
        if (data.SteamGridDbKey is { } key2 && string.IsNullOrWhiteSpace(settings.SteamGridDbKey))
            plan.Settings.Add(new SettingChange { Key = "steamGridDbKey", Label = "SteamGridDB key", After = key2 });
        return plan;
    }

    private static string Expand(string path, string? installDir)
    {
        var p = path.Replace("{InstallDir}", installDir ?? "", StringComparison.OrdinalIgnoreCase)
                    .Replace("{InstallDirName}", Path.GetFileName(installDir ?? ""), StringComparison.OrdinalIgnoreCase);
        p = Environment.ExpandEnvironmentVariables(p);
        if (!Path.IsPathRooted(p) && installDir is { Length: > 0 }) p = Path.Combine(installDir, p);
        try { return Path.GetFullPath(p); } catch { return p; }
    }

    // ---- the record, for Undo ----

    public sealed class Record
    {
        public DateTime At { get; set; }
        public string? From { get; set; }
        public List<PlaytimeChange> Playtime { get; set; } = new();
        public List<FlagChange> Flags { get; set; } = new();
        public List<CollectionChange> Collections { get; set; } = new();
        public List<string> Games { get; set; } = new();
        public List<string> Sessions { get; set; } = new();
        public List<string> Achievements { get; set; } = new();
        public List<SettingChange> Settings { get; set; } = new();

        public bool IsEmpty => Playtime.Count + Flags.Count + Collections.Count + Games.Count + Sessions.Count + Achievements.Count + Settings.Count == 0;
    }

    public static string RecordPath(string? dir = null) => Path.Combine(dir ?? Paths.DataDir, "playnite-import.json");

    public static Record? LoadRecord(string? dir = null)
    {
        try
        {
            var path = RecordPath(dir);
            return File.Exists(path) ? JsonSerializer.Deserialize<Record>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) { Log.Info($"Playnite import: record unreadable: {ex.Message}"); return null; }
    }

    private static void SaveRecord(Record r, string? dir)
    {
        try
        {
            if (r.IsEmpty) { File.Delete(RecordPath(dir)); return; }
            File.WriteAllText(RecordPath(dir), JsonSerializer.Serialize(r));
        }
        catch (Exception ex) { Log.Info($"Playnite import: record not written: {ex.Message}"); }
    }

    // ---- applying ----

    public sealed class Outcome
    {
        public int Playtime { get; set; }
        public int Flags { get; set; }
        public int Collections { get; set; }
        public int Games { get; set; }
        public int Sessions { get; set; }
        public int Achievements { get; set; }
        public int Settings { get; set; }
        public List<string> AchievementGames { get; set; } = new();
        public int Total => Playtime + Flags + Collections + Games + Sessions + Achievements + Settings;
    }

    /// <summary>Applies the parts named, adding each change to the record as it goes. Every
    /// change is checked again against the data as it is at this moment -- the plan may be a
    /// minute old -- so nothing Loungepad has gained since is overwritten either. The library is
    /// saved once; the caller saves settings and pushes state.</summary>
    public static Outcome Apply(Plan plan, ISet<string> parts, LibraryStore library, ActivityStore activity,
        AchievementStore achievements, AppSettings settings, string? recordDir = null)
    {
        var record = LoadRecord(recordDir) ?? new Record();
        record.At = DateTime.Now;
        record.From = plan.Data.Dir;
        var outcome = new Outcome();

        if (parts.Contains("games"))
            foreach (var g in plan.Games)
            {
                if (library.Find(g.Id) is not null) continue;
                library.Change(() => library.Games.Add(g));
                record.Games.Add(g.Id);
                outcome.Games++;
            }

        if (parts.Contains("playtime"))
            foreach (var c in plan.Playtime)
            {
                if (library.Find(c.GameId) is not { } g) continue;
                var change = new PlaytimeChange
                {
                    GameId = g.Id,
                    MinutesBefore = g.PlaytimeMinutes, MinutesAfter = g.PlaytimeMinutes > 0 ? g.PlaytimeMinutes : c.MinutesAfter,
                    SessionsBefore = g.Sessions, SessionsAfter = g.Sessions > 0 ? g.Sessions : c.SessionsAfter,
                    LastBefore = g.LastPlayed, LastAfter = g.LastPlayed ?? c.LastAfter,
                };
                if (change.MinutesAfter == change.MinutesBefore && change.SessionsAfter == change.SessionsBefore && change.LastAfter == change.LastBefore) continue;
                g.PlaytimeMinutes = change.MinutesAfter;
                g.Sessions = change.SessionsAfter;
                g.LastPlayed = change.LastAfter;
                record.Playtime.Add(change);
                outcome.Playtime++;
            }

        if (parts.Contains("flags"))
            foreach (var c in plan.Flags)
            {
                if (library.Find(c.GameId) is not { } g) continue;
                var done = new FlagChange { GameId = g.Id, Favorite = c.Favorite && !g.Favorite, Hidden = c.Hidden && !g.Hidden };
                if (!done.Favorite && !done.Hidden) continue;
                if (done.Favorite) g.Favorite = true;
                if (done.Hidden) g.Hidden = true;
                record.Flags.Add(done);
                outcome.Flags++;
            }

        if (parts.Contains("collections"))
        {
            var mine = record.Collections.Where(c => c.CollectionId is not null).Select(c => c.CollectionId!).ToHashSet();
            foreach (var c in plan.Collections)
            {
                // Only ever a collection this import made; the plan's is re-found by id.
                var col = c.CollectionId is { } cid && mine.Contains(cid) ? library.Collections.FirstOrDefault(x => x.Id == cid) : null;
                if (col is null)
                {
                    if (library.Collections.Any(x => x.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase))) continue;
                    var made = new CollectionDef { Id = Guid.NewGuid().ToString("N"), Name = c.Name };
                    library.Change(() => library.Collections.Add(made));
                    col = made;
                }
                var target = col;
                var add = c.GameIds.Where(id => !target.GameIds.Contains(id) && library.Find(id) is not null).ToList();
                if (add.Count == 0) continue;
                library.Change(() => target.GameIds.AddRange(add));
                record.Collections.Add(new CollectionChange { Name = col.Name, CollectionId = col.Id, GameIds = add });
                outcome.Collections++;
            }
        }

        library.Save();

        if (parts.Contains("sessions"))
        {
            // Checked again for overlap with anything recorded since the plan was made.
            var recorded = activity.All().Where(s => s.Origin is null).ToList();
            var addable = plan.Sessions.Where(x => library.Find(x.Session.GameId) is not null
                && !recorded.Any(m => m.GameId == x.Session.GameId && m.Start < x.Session.End && m.End > x.Session.Start));
            var added = activity.AddImported(addable);
            record.Sessions.AddRange(added.Select(s => s.Id));
            outcome.Sessions = added.Count;
        }

        if (parts.Contains("achievements"))
            foreach (var set in plan.Achievements)
            {
                if (achievements.Get(set.GameId) is not null || library.Find(set.GameId) is null) continue;
                achievements.Put(set);
                record.Achievements.Add(set.GameId);
                outcome.Achievements++;
                outcome.AchievementGames.Add(set.GameId);
            }

        if (parts.Contains("settings"))
            foreach (var c in plan.Settings)
                if (c.Key == "steamGridDbKey" && string.IsNullOrWhiteSpace(settings.SteamGridDbKey))
                {
                    settings.SteamGridDbKey = c.After ?? "";
                    record.Settings.Add(c);
                    outcome.Settings++;
                }

        SaveRecord(record, recordDir);
        return outcome;
    }

    /// <summary>
    /// Takes back everything the record says the import did, where it still stands: a favourite
    /// the user has since switched off stays off, a key they have since changed stays changed,
    /// and playtime comes down by what the import added rather than to the old number, so time
    /// played since the import is kept. The record is deleted.
    /// </summary>
    public static Outcome Undo(LibraryStore library, ActivityStore activity, AchievementStore achievements, AppSettings settings, string? recordDir = null)
    {
        var outcome = new Outcome();
        var r = LoadRecord(recordDir);
        if (r is null) return outcome;

        foreach (var c in r.Playtime)
        {
            if (library.Find(c.GameId) is not { } g) continue;
            g.PlaytimeMinutes = Math.Max(0, g.PlaytimeMinutes - (c.MinutesAfter - c.MinutesBefore));
            g.Sessions = Math.Max(0, g.Sessions - (c.SessionsAfter - c.SessionsBefore));
            if (g.LastPlayed == c.LastAfter) g.LastPlayed = c.LastBefore;
            outcome.Playtime++;
        }
        foreach (var c in r.Flags)
        {
            if (library.Find(c.GameId) is not { } g) continue;
            if (c.Favorite && g.Favorite) g.Favorite = false;
            if (c.Hidden && g.Hidden) g.Hidden = false;
            outcome.Flags++;
        }
        foreach (var c in r.Collections)
        {
            var col = library.Collections.FirstOrDefault(x => x.Id == c.CollectionId);
            if (col is null) continue;
            library.Change(() =>
            {
                col.GameIds.RemoveAll(c.GameIds.Contains);
                if (col.GameIds.Count == 0) library.Collections.Remove(col);
            });
            outcome.Collections++;
        }
        foreach (var id in r.Games)
        {
            if (library.Find(id) is null) continue;
            library.Change(() =>
            {
                library.Games.RemoveAll(g => g.Id == id);
                foreach (var col in library.Collections) col.GameIds.Remove(id);
            });
            outcome.Games++;
        }
        library.Save();

        outcome.Sessions = activity.RemoveMany(r.Sessions.ToHashSet());
        foreach (var gid in r.Achievements)
            if (achievements.Get(gid) is { Source: Origin })
            {
                achievements.Remove(gid);
                outcome.Achievements++;
                outcome.AchievementGames.Add(gid);
            }

        foreach (var c in r.Settings)
            if (c.Key == "steamGridDbKey" && settings.SteamGridDbKey == c.After) { settings.SteamGridDbKey = ""; outcome.Settings++; }

        try { File.Delete(RecordPath(recordDir)); } catch { /* next undo finds nothing to do */ }
        return outcome;
    }
}

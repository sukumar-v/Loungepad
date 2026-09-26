using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Loungepad.Interop;
using Loungepad.Models;
using Microsoft.Win32;

namespace Loungepad.Services;

/// <summary>
/// The actions: what actions.json holds merged over the packs, which of the packs' programs are
/// on this PC, the window in front resolved to one of them, the button bindings the gamepad
/// loop asks about on every poll, and the sending of a shortcut.
///
/// Two threads touch it. The UI thread edits (the bridge's action* commands) and the pad thread
/// reads (<see cref="Evaluate"/>, 125 times a second), so every edit rebuilds an immutable
/// <see cref="Snapshot"/> and swaps it in; the pad thread never sees a list mid-edit.
/// </summary>
internal class ActionService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private static readonly string FilePath = Path.Combine(Paths.DataDir, "actions.json");

    /// <summary>The file: only what differs from the packs, plus the user's own apps.</summary>
    private ActionsFileData _file = new();
    /// <summary>Which pack programs detection found, by app id, with the exe it found.</summary>
    private Dictionary<string, string> _found = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _icons = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private volatile Snapshot _snapshot = new(Array.Empty<ActionApp>());
    private readonly Func<IReadOnlyList<EmulatorDef>> _emulators;

    /// <summary>What sends the keys. Replaceable so a harness can record instead of typing into
    /// whatever is in front.</summary>
    public Action<string> SendKeys = keys => ShortcutKeys.Send(keys);

    public ActionService(Func<IReadOnlyList<EmulatorDef>> emulators)
    {
        _emulators = emulators;
    }

    // ---- the file ----

    public void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                _file = JsonSerializer.Deserialize<ActionsFileData>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception ex)
        {
            Log.Info($"Actions load failed, starting from the packs: {ex.Message}");
            _file = new();
        }
        _file.Apps ??= new();
        foreach (var app in _file.Apps)
        {
            app.Actions ??= new();
            app.Exes ??= new();
        }
        Rebuild();
    }

    private void Save()
    {
        try
        {
            Paths.EnsureCreated();
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_file, JsonOpts));
        }
        catch (Exception ex) { Log.Info($"Actions save failed: {ex.Message}"); }
    }

    // ---- the resolved list ----

    /// <summary>Every app as the page and the pad see it: packs merged with the file, custom apps
    /// after. <see cref="Installed"/> says whether it belongs in the Settings grid.</summary>
    public IReadOnlyList<ActionApp> Apps => _snapshot.Apps;

    public ActionApp? Find(string id) => _snapshot.Apps.FirstOrDefault(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>A pack the user has not touched has no entry in the file.</summary>
    public bool Modified(string id) => _file.Apps.Any(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Shown in Settings: Everywhere always, a pack when its program was found or the user
    /// added it by hand, and every custom app.</summary>
    public bool Installed(ActionApp app) =>
        app.Id == ActionPacks.EverywhereId || app.Custom || app.Pinned || _found.ContainsKey(app.Id);

    public string? Icon(ActionApp app)
    {
        lock (_gate) return _icons.TryGetValue(app.Id, out var i) ? i : null;
    }

    /// <summary>The app an exe name belongs to, or null. Everywhere is never the answer here.</summary>
    public ActionApp? ForExe(string? exe) => _snapshot.ForExe(exe);

    private void Rebuild()
    {
        var list = new List<ActionApp>();
        foreach (var pack in ActionPacks.All)
        {
            var mine = _file.Apps.FirstOrDefault(a => a.Id.Equals(pack.Id, StringComparison.OrdinalIgnoreCase));
            list.Add(mine is null ? pack.Clone() : Merge(pack, mine));
        }
        foreach (var mine in _file.Apps)
        {
            if (ActionPacks.Find(mine.Id) is not null) continue;
            var app = mine.Clone();
            app.Custom = true;
            foreach (var a in app.Actions) a.Custom = true;
            list.Add(app);
        }
        _snapshot = new Snapshot(list);
    }

    /// <summary>The pack's list in the pack's order, each action replaced by the file's copy when
    /// there is one; the file's own actions after. A file entry for an action the pack no longer
    /// has is kept, as a custom one, rather than silently dropped.</summary>
    private static ActionApp Merge(ActionApp pack, ActionApp mine)
    {
        var app = pack.Clone();
        app.Pinned = mine.Pinned;
        if (!string.IsNullOrEmpty(mine.Path)) app.Path = mine.Path;
        var merged = new List<ActionDef>();
        foreach (var d in app.Actions)
        {
            var m = mine.Actions.FirstOrDefault(x => x.Id == d.Id);
            if (m is null) { merged.Add(d); continue; }
            var c = m.Clone();
            c.Custom = false;
            c.Danger = d.Danger;
            merged.Add(c);
        }
        foreach (var m in mine.Actions)
        {
            if (app.Actions.Any(d => d.Id == m.Id)) continue;
            var c = m.Clone();
            c.Custom = true;
            merged.Add(c);
        }
        app.Actions = merged;
        return app;
    }

    // ---- edits, from the bridge ----

    private ActionApp FileEntry(string id)
    {
        var e = _file.Apps.FirstOrDefault(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (e is not null) return e;
        var pack = ActionPacks.Find(id) ?? throw new InvalidOperationException($"No such app: {id}");
        e = new ActionApp { Id = pack.Id, Name = pack.Name, Exes = new List<string>(pack.Exes) };
        _file.Apps.Add(e);
        return e;
    }

    /// <summary>Add or replace one action. Returns what was stored, or null when refused.</summary>
    public ActionDef? Update(string appId, ActionDef incoming)
    {
        var app = Find(appId);
        if (app is null) return null;
        var id = (incoming.Id ?? "").Trim();
        if (id.Length == 0 || id.Length > 40 || !id.All(c => char.IsLetterOrDigit(c) || c == '-')) return null;
        var clean = new ActionDef
        {
            Id = id,
            Name = Truncate((incoming.Name ?? "").Trim(), 48),
            Keys = ShortcutKeys.IsValid(incoming.Keys ?? "") ? Normalize(incoming.Keys!) : "",
            Button = CleanButton(incoming.Button),
            Hidden = incoming.Hidden,
        };
        if (clean.Name.Length == 0) clean.Name = clean.Keys.Length > 0 ? clean.Keys : "Action";

        var entry = FileEntry(app.Id);
        var i = entry.Actions.FindIndex(a => a.Id == id);
        if (i >= 0) entry.Actions[i] = clean; else entry.Actions.Add(clean);
        Save();
        Rebuild();
        return clean;
    }

    public bool Remove(string appId, string actionId)
    {
        var app = Find(appId);
        if (app is null) return false;
        var packHasIt = ActionPacks.Find(app.Id)?.Actions.Any(a => a.Id == actionId) == true;
        if (packHasIt) return false;   // a pack's action is hidden or reset, never deleted
        var entry = _file.Apps.FirstOrDefault(a => a.Id.Equals(app.Id, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return false;
        var n = entry.Actions.RemoveAll(a => a.Id == actionId);
        if (n == 0) return false;
        Save();
        Rebuild();
        return true;
    }

    /// <summary>Back to the pack: the file's entry goes, and with it every change and every custom
    /// action. Only for packs; a custom app has nothing to go back to.</summary>
    public bool Reset(string appId)
    {
        if (ActionPacks.Find(appId) is null) return false;
        var n = _file.Apps.RemoveAll(a => a.Id.Equals(appId, StringComparison.OrdinalIgnoreCase));
        if (n == 0) return false;
        Save();
        Rebuild();
        return true;
    }

    /// <summary>
    /// Add a program: by its exe (from an open window) or its path (from the file dialog). An exe
    /// we have a pack for pins the pack; anything else becomes an empty custom app. Returns the
    /// app's id, and whether it was already there.
    /// </summary>
    public (string Id, bool Existed) AddApp(string exeOrPath, string? name)
    {
        var path = exeOrPath.Contains('\\') || exeOrPath.Contains('/') ? exeOrPath : null;
        var exe = ExeName(exeOrPath);
        if (exe.Length == 0) throw new ArgumentException("No program named");

        if (ActionPacks.ForExe(exe) is { } pack)
        {
            var existed = Installed(Find(pack.Id)!);
            var entry = FileEntry(pack.Id);
            entry.Pinned = true;
            if (path is not null) entry.Path = path;
            Save();
            Rebuild();
            return (pack.Id, existed);
        }

        if (_snapshot.ForExe(exe) is { } have) return (have.Id, true);

        var app = new ActionApp
        {
            Id = exe,
            Name = Truncate((name ?? "").Trim().Length > 0 ? name!.Trim() : PrettyName(path, exe), 48),
            Exes = new List<string> { exe },
            Path = path,
            Custom = true,
        };
        _file.Apps.Add(app);
        Save();
        Rebuild();
        return (app.Id, false);
    }

    public bool RemoveApp(string appId)
    {
        var app = Find(appId);
        if (app is null || app.Id == ActionPacks.EverywhereId) return false;
        if (!app.Custom)
        {
            // A pack that was added by hand: unpin it. One found on the PC cannot be removed, only
            // reset; the grid would put it straight back.
            var entry = _file.Apps.FirstOrDefault(a => a.Id.Equals(app.Id, StringComparison.OrdinalIgnoreCase));
            if (entry is null || !entry.Pinned) return false;
            entry.Pinned = false;
            Save();
            Rebuild();
            return true;
        }
        var n = _file.Apps.RemoveAll(a => a.Id.Equals(app.Id, StringComparison.OrdinalIgnoreCase));
        if (n == 0) return false;
        lock (_gate) _icons.Remove(app.Id);
        Save();
        Rebuild();
        return true;
    }

    public bool RenameApp(string appId, string name)
    {
        var app = Find(appId);
        if (app is null || !app.Custom) return false;
        var entry = _file.Apps.FirstOrDefault(a => a.Id.Equals(app.Id, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return false;
        name = Truncate(name.Trim(), 48);
        if (name.Length == 0) return false;
        entry.Name = name;
        Save();
        Rebuild();
        return true;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    /// <summary>"Ctrl+Shift+T" spelt the way the page spells it, whatever was typed.</summary>
    private static string Normalize(string keys)
    {
        var parts = ShortcutKeys.Parts(keys);
        var order = new[] { "Ctrl", "Shift", "Alt", "Win" };
        var mods = order.Where(m => parts.Take(parts.Count - 1).Contains(m, StringComparer.OrdinalIgnoreCase));
        return string.Join("+", mods.Append(parts[^1]));
    }

    /// <summary>A button combo in the launcher's own names and order, or null. Unknown names
    /// are dropped rather than kept as text that would never match a press.</summary>
    public static string? CleanButton(string? button)
    {
        if (string.IsNullOrWhiteSpace(button)) return null;
        var parts = button.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(CanonButton).Where(p => p is not null).Select(p => p!).Distinct().ToList();
        if (parts.Count == 0) return null;
        return string.Join(" + ", ButtonOrder.Where(parts.Contains));
    }

    /// <summary>The order a combo is written in, so "Y + LB" and "LB + Y" are one binding.</summary>
    public static readonly string[] ButtonOrder =
        { "LT", "RT", "LB", "RB", "LS", "RS", "View", "Menu", "Guide", "A", "B", "X", "Y", "Up", "Down", "Left", "Right" };

    private static string? CanonButton(string p) => p.ToUpperInvariant() switch
    {
        "A" => "A", "B" => "B", "X" => "X", "Y" => "Y",
        "LB" => "LB", "RB" => "RB", "LT" => "LT", "RT" => "RT", "LS" => "LS", "RS" => "RS",
        "VIEW" or "BACK" => "View", "MENU" or "START" => "Menu", "GUIDE" or "XBOX" or "PS" => "Guide",
        "UP" => "Up", "DOWN" => "Down", "LEFT" => "Left", "RIGHT" => "Right",
        _ => null,
    };

    /// <summary>Bindings that can never fire: the click buttons on their own, and the Guide button,
    /// which Windows keeps. The page refuses them too; this is for a hand-edited file.</summary>
    public static bool IsReservedButton(string? combo) => combo is "A" or "B" or "Guide";

    // ---- what is on this PC ----

    /// <summary>
    /// Look for each pack's program: App Paths, the uninstall entries, the places the odd one
    /// installs to, the Store's execution aliases, the library's own emulators, and whatever is
    /// running right now. Then an icon for everything found or added. Off the UI thread: the
    /// registry walk is quick, the icons are a few milliseconds each.
    /// </summary>
    public void Detect()
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Consider(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            var exe = ExeName(path);
            if (ActionPacks.ForExe(exe) is not { } pack || found.ContainsKey(pack.Id)) return;
            if (!SafeExists(path)) return;
            found[pack.Id] = path;
        }

        foreach (var pack in ActionPacks.All)
        {
            foreach (var exe in pack.Exes)
            {
                Consider(AppPath(exe));
                Consider(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", exe + ".exe"));
            }
        }
        Consider(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"));
        foreach (var candidate in KnownPaths()) Consider(candidate);
        try { foreach (var exe in EmulatorDetection.RegistryExes()) Consider(exe); }
        catch (Exception ex) { Log.Info($"Actions: uninstall entries unreadable: {ex.Message}"); }
        try { foreach (var emu in _emulators()) Consider(emu.ExePath); }
        catch { /* the library is the UI thread's; a torn read here only costs a detection */ }
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (ActionPacks.ForExe(p.ProcessName) is { } pack && !found.ContainsKey(pack.Id))
                        found[pack.Id] = ProcessPath((uint)p.Id) ?? "";
                }
                finally { p.Dispose(); }
            }
        }
        catch { /* process list unavailable */ }

        _found = found;

        // Icons: for every app that will be in the grid and has a program we can point at.
        foreach (var app in _snapshot.Apps)
        {
            if (app.Id == ActionPacks.EverywhereId) continue;
            var path = app.Path;
            if (string.IsNullOrEmpty(path) && found.TryGetValue(app.Id, out var f)) path = f;
            if (string.IsNullOrEmpty(path) && app.Custom) path = RunningPath(app.Exes);
            bool have;
            lock (_gate) have = _icons.ContainsKey(app.Id);
            if (have || string.IsNullOrEmpty(path)) continue;
            var icon = ExtractIcon(path);
            lock (_gate) _icons[app.Id] = icon;
        }
        Log.Info($"Actions: {found.Count} of {ActionPacks.All.Count - 1} pack programs found");
    }

    /// <summary>HKLM/HKCU App Paths: where most installers register their exe by name.</summary>
    private static string? AppPath(string exe)
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            foreach (var sub in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\" })
            {
                try
                {
                    using var key = hive.OpenSubKey(sub + exe + ".exe");
                    if (key?.GetValue(null) is string path && path.Length > 0)
                        return Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
                }
                catch { /* one unreadable key */ }
            }
        }
        return null;
    }

    /// <summary>The installs that register nothing by exe name.</summary>
    private static IEnumerable<string> KnownPaths()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        yield return Path.Combine(roaming, "Spotify", "Spotify.exe");
        yield return Path.Combine(pf, "VideoLAN", "VLC", "vlc.exe");
        yield return Path.Combine(pf86, "VideoLAN", "VLC", "vlc.exe");
        yield return Path.Combine(pf, "Mozilla Firefox", "firefox.exe");
        yield return Path.Combine(pf, "Google", "Chrome", "Application", "chrome.exe");
        yield return Path.Combine(pf86, "Google", "Chrome", "Application", "chrome.exe");
        yield return Path.Combine(pf86, "Microsoft", "Edge", "Application", "msedge.exe");
        yield return Path.Combine(pf, "BraveSoftware", "Brave-Browser", "Application", "brave.exe");
        yield return Path.Combine(local, "Programs", "Opera", "opera.exe");
        yield return Path.Combine(local, "Programs", "Opera GX", "opera.exe");
        yield return Path.Combine(local, "Vivaldi", "Application", "vivaldi.exe");
        // Discord keeps its exe under a versioned folder; the newest is the one that runs.
        string? discord = null;
        try
        {
            var root = Path.Combine(local, "Discord");
            if (Directory.Exists(root))
                discord = Directory.GetDirectories(root, "app-*").OrderByDescending(d => d).Select(d => Path.Combine(d, "Discord.exe")).FirstOrDefault(File.Exists);
        }
        catch { /* not there */ }
        if (discord is not null) yield return discord;
    }

    private static string? RunningPath(IEnumerable<string> exes)
    {
        try
        {
            foreach (var exe in exes)
            {
                foreach (var p in Process.GetProcessesByName(exe))
                {
                    try { return ProcessPath((uint)p.Id); }
                    finally { p.Dispose(); }
                }
            }
        }
        catch { /* nothing running */ }
        return null;
    }

    private static bool SafeExists(string path)
    {
        try { return File.Exists(path); }
        catch { return false; }
    }

    /// <summary>"C:\...\firefox.exe" or "FIREFOX.EXE" or "firefox" → "firefox".</summary>
    public static string ExeName(string exeOrPath)
    {
        var name = exeOrPath.Trim().Trim('"');
        try { name = Path.GetFileName(name); } catch { /* keep as is */ }
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name.ToLowerInvariant();
    }

    private static string PrettyName(string? path, string exe)
    {
        if (path is not null)
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                var d = (info.FileDescription ?? "").Trim();
                if (d.Length > 0 && d.Length <= 48) return d;
            }
            catch { /* no version resource */ }
        }
        return exe.Length > 0 ? char.ToUpperInvariant(exe[0]) + exe[1..] : exe;
    }

    private static string? ExtractIcon(string path)
    {
        var icons = new IntPtr[1];
        var ids = new uint[1];
        try
        {
            var n = NativeMethods.PrivateExtractIcons(path, 0, 96, 96, icons, ids, 1, 0);
            if (n == 0 || icons[0] == IntPtr.Zero)
            {
                n = NativeMethods.PrivateExtractIcons(path, 0, 48, 48, icons, ids, 1, 0);
                if (n == 0 || icons[0] == IntPtr.Zero) return null;
            }
            return WindowService.EncodeIcon(icons[0]);
        }
        catch (Exception ex)
        {
            Log.Info($"Actions: icon for {path} failed: {ex.Message}");
            return null;
        }
        finally
        {
            if (icons[0] != IntPtr.Zero) NativeMethods.DestroyIcon(icons[0]);
        }
    }

    // ---- the window in front ----

    /// <summary>
    /// The exe name behind a window, as the packs name them. A Store app's top-level window is
    /// the frame host's; the app itself owns the CoreWindow inside it, and that is the process
    /// that matters.
    /// </summary>
    public static string ExeOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "";
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return "";
        var name = ProcessName(pid);
        if (name.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
        {
            uint inner = 0;
            NativeMethods.EnumChildWindows(hwnd, (child, _) =>
            {
                var cls = new StringBuilder(64);
                NativeMethods.GetClassName(child, cls, cls.Capacity);
                if (cls.ToString() != "Windows.UI.Core.CoreWindow") return true;
                NativeMethods.GetWindowThreadProcessId(child, out inner);
                return inner == 0;
            }, IntPtr.Zero);
            if (inner != 0 && inner != pid) name = ProcessName(inner);
        }
        return name.ToLowerInvariant();
    }

    private static string ProcessName(uint pid)
    {
        var path = ProcessPath(pid);
        if (path is not null) return ExeName(path);
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch { return ""; }
    }

    private static string? ProcessPath(uint pid)
    {
        var h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, (int)pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            uint len = (uint)sb.Capacity;
            return NativeMethods.QueryFullProcessImageName(h, 0, sb, ref len) ? sb.ToString(0, (int)len) : null;
        }
        finally { NativeMethods.CloseHandle(h); }
    }

    // ---- bindings, on the pad thread ----

    private IntPtr _fgHwnd;
    private Snapshot? _fgSnapshot;
    private ActionApp? _fgApp;
    private readonly HashSet<string> _satisfied = new();

    /// <summary>
    /// Called once per poll while the pad is a desktop device: the launcher not in front, no
    /// focused game, the on-screen keyboard not driving. Fires the binding that the pad state has
    /// just completed and returns the mask of buttons it used, so the same press is not also a
    /// mouse click. <paramref name="spent"/> is what the loop already spent this poll (the keyboard
    /// toggle); <paramref name="reserved"/> the menu and screenshot combos, which always win.
    /// </summary>
    public ushort Evaluate(in NativeMethods.XINPUT_GAMEPAD pad, ushort spent, IReadOnlyList<ushort> reserved)
    {
        var snap = _snapshot;
        var fg = NativeMethods.GetForegroundWindow();
        if (fg != _fgHwnd || !ReferenceEquals(snap, _fgSnapshot))
        {
            _fgHwnd = fg;
            _fgSnapshot = snap;
            _fgApp = snap.ForExe(ExeOf(fg));
            _satisfied.Clear();
        }

        Binding? fire = null;
        var nowSatisfied = new List<string>();
        foreach (var b in snap.BindingsFor(_fgApp))
        {
            if (!GamepadService.ComboPressed(pad, b.Combo)) continue;
            nowSatisfied.Add(b.Key);
            if (_satisfied.Contains(b.Key)) continue;               // held since last time
            if ((b.Mask & spent) != 0) continue;                    // the loop spent this press
            if (b.Mask != 0 && reserved.Any(r => r == b.Mask)) continue;
            if (fire is null || b.Parts > fire.Parts) fire = b;     // the longest chord wins
        }
        _satisfied.Clear();
        foreach (var k in nowSatisfied) _satisfied.Add(k);
        if (fire is null) return 0;

        // Everything the chord holds counts as satisfied now, so "Y" does not fire on the same
        // press that completed "LB + Y".
        foreach (var b in snap.BindingsFor(_fgApp))
            if ((b.Mask & fire.Mask) != 0) _satisfied.Add(b.Key);

        try
        {
            SendKeys(fire.Keys);
            Log.Info($"Action: {fire.Combo} → {fire.Keys} ({fire.Name}) in {(_fgApp?.Name ?? "no app")}");
        }
        catch (Exception ex) { Log.Info($"Action {fire.Name} failed: {ex.Message}"); }
        return fire.Mask;
    }

    /// <summary>Forget the held chords: called when the pad stops being a desktop device.</summary>
    public void ResetBindings() => _satisfied.Clear();

    private sealed record Binding(string Key, string Combo, ushort Mask, int Parts, string Keys, string Name);

    /// <summary>The resolved apps plus their bindings, built once per edit and read without locks.</summary>
    private sealed class Snapshot
    {
        public IReadOnlyList<ActionApp> Apps { get; }
        private readonly Dictionary<string, ActionApp> _byExe = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Binding[]> _bindings = new(StringComparer.OrdinalIgnoreCase);
        private readonly Binding[] _everywhere;

        public Snapshot(IReadOnlyList<ActionApp> apps)
        {
            Apps = apps;
            foreach (var app in apps)
            {
                foreach (var exe in app.Exes) _byExe.TryAdd(exe, app);
                _bindings[app.Id] = app.Actions
                    .Where(a => !string.IsNullOrEmpty(a.Button) && !IsReservedButton(a.Button) && ShortcutKeys.IsValid(a.Keys))
                    .Select(a => new Binding(app.Id + "/" + a.Id, a.Button!, GamepadService.ComboMask(a.Button),
                        a.Button!.Split('+').Length, a.Keys, a.Name))
                    .ToArray();
            }
            _everywhere = _bindings.TryGetValue(ActionPacks.EverywhereId, out var e) ? e : Array.Empty<Binding>();
        }

        public ActionApp? ForExe(string? exe) =>
            !string.IsNullOrEmpty(exe) && _byExe.TryGetValue(exe, out var app) ? app : null;

        /// <summary>The app's own bindings, then Everywhere's minus any combo the app already
        /// uses: the app's binding for a button wins over the general one.</summary>
        public IEnumerable<Binding> BindingsFor(ActionApp? app)
        {
            if (app is null || !_bindings.TryGetValue(app.Id, out var own)) return _everywhere;
            return own.Concat(_everywhere.Where(e => !own.Any(o => o.Combo == e.Combo)));
        }
    }
}

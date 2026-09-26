using System.Text.Json.Serialization;
using Loungepad.Interop;

namespace Loungepad.Models;

/// <summary>
/// One program's actions: a keyboard shortcut the program understands, given a name, an optional
/// gamepad button that fires it from anywhere in that program, and a place on the action wheel.
///
/// The wheel is context aware: which app's actions it shows is decided by the window that was in
/// front when the Power Wheel opened, matched on the process's exe name. "Everywhere" is the one
/// app that matches every window and holds what is true of Windows itself -- volume, media keys,
/// Alt+Tab. Bundled packs (<see cref="ActionPacks"/>) are the starting point for the programs we
/// know; everything in them can be changed, hidden or given a button, and the file only ever
/// holds what differs from the pack, so a new default arrives on the next build without
/// overwriting anything the user set.
/// </summary>
public class ActionApp
{
    /// <summary>The primary exe name, lower case, without ".exe" -- "firefox". Custom apps use
    /// theirs too, so an app added by hand that turns out to be one we have a pack for becomes
    /// that pack rather than a second copy of it.</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Every exe name the app runs as, lower case, without ".exe".</summary>
    public List<string> Exes { get; set; } = new();
    /// <summary>The program's path when known, for its icon. Never used to start anything.</summary>
    public string? Path { get; set; }
    /// <summary>Added by the user rather than shipped as a pack.</summary>
    public bool Custom { get; set; }
    /// <summary>A pack the user added by hand: shown whether or not detection finds the program.</summary>
    public bool Pinned { get; set; }
    public List<ActionDef> Actions { get; set; } = new();

    public ActionApp Clone() => new()
    {
        Id = Id, Name = Name, Exes = new List<string>(Exes), Path = Path, Custom = Custom, Pinned = Pinned,
        Actions = Actions.Select(a => a.Clone()).ToList(),
    };
}

public class ActionDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>The shortcut, as "Ctrl+Shift+T": zero or more of Ctrl, Shift, Alt, Win, then one
    /// key by the names <see cref="ShortcutKeys"/> knows. Empty means the action has no shortcut
    /// yet and cannot fire.</summary>
    public string Keys { get; set; } = "";
    /// <summary>A gamepad button or combo -- "Y", "LB + Y" -- that fires this action while the app
    /// is in front and the launcher is not. Null or empty for none.</summary>
    public string? Button { get; set; }
    /// <summary>Kept off the wheel. The button still works; the row still shows in Settings.</summary>
    public bool Hidden { get; set; }
    /// <summary>Added by the user rather than shipped in the pack.</summary>
    public bool Custom { get; set; }
    /// <summary>Drawn red on the wheel: closes or quits something.</summary>
    public bool Danger { get; set; }

    public ActionDef Clone() => new()
    {
        Id = Id, Name = Name, Keys = Keys, Button = Button, Hidden = Hidden, Custom = Custom, Danger = Danger,
    };
}

/// <summary>What actions.json holds: only what differs from the packs, plus the user's own apps.</summary>
public class ActionsFileData
{
    public int Version { get; set; } = 1;
    public List<ActionApp> Apps { get; set; } = new();
}

/// <summary>
/// A keyboard shortcut as text, and how it is sent.
///
/// "Ctrl+Shift+T": the modifiers by name, then one key. Letters and digits are their own virtual
/// keys on every layout; a punctuation character is looked up on the current layout at send time
/// (VkKeyScanW), so "Ctrl+=" zooms a browser on AZERTY too; everything else is a named key from
/// the table below. Sent through SendKeyCombo, which carries scan codes as well as virtual keys,
/// so a program reading raw input sees the same press a real keyboard would make.
/// </summary>
public static class ShortcutKeys
{
    public readonly record struct Parsed(ushort Vk, bool Extended, ushort[] Modifiers, string Key, bool SystemWide);

    /// <summary>Named keys: the virtual key and whether it is an extended key (E0-prefixed scan code).</summary>
    private static readonly Dictionary<string, (ushort Vk, bool Ext)> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Enter"] = (NativeMethods.VK_RETURN, false), ["Tab"] = (NativeMethods.VK_TAB, false),
        ["Esc"] = (NativeMethods.VK_ESCAPE, false), ["Escape"] = (NativeMethods.VK_ESCAPE, false),
        ["Space"] = (NativeMethods.VK_SPACE, false), ["Backspace"] = (NativeMethods.VK_BACK, false),
        ["Delete"] = (NativeMethods.VK_DELETE, true), ["Insert"] = (NativeMethods.VK_INSERT, true),
        ["Home"] = (NativeMethods.VK_HOME, true), ["End"] = (NativeMethods.VK_END, true),
        ["PageUp"] = (NativeMethods.VK_PRIOR, true), ["PageDown"] = (NativeMethods.VK_NEXT, true),
        ["Up"] = (NativeMethods.VK_UP, true), ["Down"] = (NativeMethods.VK_DOWN, true),
        ["Left"] = (NativeMethods.VK_LEFT, true), ["Right"] = (NativeMethods.VK_RIGHT, true),
        ["CapsLock"] = (NativeMethods.VK_CAPITAL, false), ["NumLock"] = (NativeMethods.VK_NUMLOCK, true),
        ["ScrollLock"] = (NativeMethods.VK_SCROLL, false), ["Pause"] = (NativeMethods.VK_PAUSE, false),
        ["PrintScreen"] = (NativeMethods.VK_SNAPSHOT, true), ["Apps"] = (NativeMethods.VK_APPS, true),
        ["NumAdd"] = (NativeMethods.VK_ADD, false), ["NumSubtract"] = (NativeMethods.VK_SUBTRACT, false),
        ["NumMultiply"] = (NativeMethods.VK_MULTIPLY, false), ["NumDivide"] = (NativeMethods.VK_DIVIDE, true),
        ["NumDecimal"] = (NativeMethods.VK_DECIMAL, false), ["NumEnter"] = (NativeMethods.VK_RETURN, true),
        ["VolumeUp"] = (NativeMethods.VK_VOLUME_UP, true), ["VolumeDown"] = (NativeMethods.VK_VOLUME_DOWN, true),
        ["VolumeMute"] = (NativeMethods.VK_VOLUME_MUTE, true),
        ["MediaPlayPause"] = (NativeMethods.VK_MEDIA_PLAY_PAUSE, true), ["MediaNext"] = (NativeMethods.VK_MEDIA_NEXT_TRACK, true),
        ["MediaPrev"] = (NativeMethods.VK_MEDIA_PREV_TRACK, true), ["MediaStop"] = (NativeMethods.VK_MEDIA_STOP, true),
        ["BrowserBack"] = (NativeMethods.VK_BROWSER_BACK, true), ["BrowserForward"] = (NativeMethods.VK_BROWSER_FORWARD, true),
        ["BrowserRefresh"] = (NativeMethods.VK_BROWSER_REFRESH, true), ["BrowserHome"] = (NativeMethods.VK_BROWSER_HOME, true),
        ["BrowserSearch"] = (NativeMethods.VK_BROWSER_SEARCH, true), ["BrowserFavorites"] = (NativeMethods.VK_BROWSER_FAVORITES, true),
    };

    /// <summary>Keys that act on the system rather than on the window in front, so they can be
    /// sent with nothing in front of the launcher. A Win chord is the other kind.</summary>
    private static readonly HashSet<string> SystemKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "VolumeUp", "VolumeDown", "VolumeMute", "MediaPlayPause", "MediaNext", "MediaPrev", "MediaStop",
    };

    /// <summary>Split "Ctrl+Shift+T" into its parts. The key is the last part; "+" on its own is a key.</summary>
    public static List<string> Parts(string keys)
    {
        var list = new List<string>();
        var s = (keys ?? "").Trim();
        if (s.Length == 0) return list;
        int i = 0;
        while (i < s.Length)
        {
            int j = s.IndexOf('+', i);
            // A "+" that is the key itself: at the end, or followed by nothing but whitespace.
            if (j < 0 || j == s.Length - 1) { list.Add(s[i..].Trim()); break; }
            var part = s[i..j].Trim();
            if (part.Length > 0) list.Add(part);
            else list.Add("+");
            i = j + 1;
        }
        return list.Where(p => p.Length > 0).ToList();
    }

    public static bool IsModifier(string part) =>
        part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || part.Equals("Shift", StringComparison.OrdinalIgnoreCase)
        || part.Equals("Alt", StringComparison.OrdinalIgnoreCase) || part.Equals("Win", StringComparison.OrdinalIgnoreCase);

    /// <summary>Null when the text is not a shortcut we can send.</summary>
    public static Parsed? Parse(string keys)
    {
        var parts = Parts(keys);
        if (parts.Count == 0) return null;
        var mods = new List<ushort>();
        bool win = false;
        for (int i = 0; i < parts.Count - 1; i++)
        {
            var p = parts[i];
            if (p.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)) mods.Add(NativeMethods.VK_CONTROL);
            else if (p.Equals("Shift", StringComparison.OrdinalIgnoreCase)) mods.Add(NativeMethods.VK_SHIFT);
            else if (p.Equals("Alt", StringComparison.OrdinalIgnoreCase)) mods.Add(NativeMethods.VK_MENU);
            else if (p.Equals("Win", StringComparison.OrdinalIgnoreCase)) { mods.Add(NativeMethods.VK_LWIN); win = true; }
            else return null;
        }
        var key = parts[^1];
        if (IsModifier(key)) return null;

        ushort vk; bool ext = false;
        if (key.Length == 1)
        {
            char c = char.ToUpperInvariant(key[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') vk = c;
            else
            {
                // Punctuation goes by the layout in use. The high byte says which modifiers type
                // the character; Shift is added when it is needed and not already held, so "Ctrl++"
                // on a layout where + is Shift+= still sends Ctrl+Shift+=.
                short scan = NativeMethods.VkKeyScanW(key[0]);
                if (scan == -1) return null;
                vk = (ushort)(scan & 0xFF);
                if ((scan & 0x100) != 0 && !mods.Contains(NativeMethods.VK_SHIFT)) mods.Add(NativeMethods.VK_SHIFT);
            }
        }
        else if (key.Length is 2 or 3 && (key[0] == 'F' || key[0] == 'f') && int.TryParse(key[1..], out var fn) && fn is >= 1 and <= 24)
            vk = (ushort)(NativeMethods.VK_F1 + fn - 1);
        else if (key.Length == 4 && key.StartsWith("Num", StringComparison.OrdinalIgnoreCase) && char.IsDigit(key[3]))
            vk = (ushort)(NativeMethods.VK_NUMPAD0 + (key[3] - '0'));
        else if (Named.TryGetValue(key, out var named)) { vk = named.Vk; ext = named.Ext; }
        else return null;

        return new Parsed(vk, ext, mods.ToArray(), key, win || SystemKeys.Contains(key));
    }

    public static bool IsValid(string keys) => Parse(keys) is not null;

    /// <summary>True for a shortcut that needs no particular window in front: media keys, and any
    /// Win chord, which the shell takes whoever has the focus.</summary>
    public static bool IsSystemWide(string keys) => Parse(keys)?.SystemWide ?? false;

    /// <summary>Press it. False when the text is not a shortcut.</summary>
    public static bool Send(string keys)
    {
        if (Parse(keys) is not { } p) return false;
        NativeMethods.SendKeyCombo(p.Vk, p.Extended, p.Modifiers);
        return true;
    }
}

/// <summary>
/// The packs: what we know the common programs answer to. Every shortcut here is the program's
/// own default binding; a button is only pre-assigned where the reach is obvious and the button
/// is free on the desktop (A and B click, RB raises the keyboard, RT boosts the pointer). Hidden
/// entries are real and bindable, just not worth a spoke on the wheel by default.
/// </summary>
public static class ActionPacks
{
    public const string EverywhereId = "everywhere";

    private static ActionDef A(string id, string name, string keys, string? button = null, bool hidden = false, bool danger = false) =>
        new() { Id = id, Name = name, Keys = keys, Button = button, Hidden = hidden, Danger = danger };

    private static ActionApp App(string id, string name, string[] exes, params ActionDef[] actions) =>
        new() { Id = id, Name = name, Exes = exes.ToList(), Actions = actions.ToList() };

    /// <summary>The browsers share one list; only the odd shortcut differs.</summary>
    private static ActionDef[] Browser(string downloads, string privateWindow) => new[]
    {
        A("new-tab", "New tab", "Ctrl+T", "Y"),
        A("close-tab", "Close tab", "Ctrl+W"),
        A("reopen-tab", "Reopen closed tab", "Ctrl+Shift+T"),
        A("next-tab", "Next tab", "Ctrl+Tab"),
        A("prev-tab", "Previous tab", "Ctrl+Shift+Tab"),
        A("back", "Back", "Alt+Left", "X"),
        A("forward", "Forward", "Alt+Right"),
        A("reload", "Reload", "F5"),
        A("address", "Address bar", "Ctrl+L"),
        A("find", "Find in page", "Ctrl+F"),
        A("fullscreen", "Full screen", "F11"),
        A("zoom-in", "Zoom in", "Ctrl+="),
        A("zoom-out", "Zoom out", "Ctrl+-"),
        A("zoom-reset", "Reset zoom", "Ctrl+0", hidden: true),
        A("bookmark", "Bookmark this page", "Ctrl+D", hidden: true),
        A("history", "History", "Ctrl+H", hidden: true),
        A("downloads", "Downloads", downloads, hidden: true),
        A("new-window", "New window", "Ctrl+N", hidden: true),
        A("private", "Private window", privateWindow, hidden: true),
        A("home", "Home page", "Alt+Home", hidden: true),
    };

    public static IReadOnlyList<ActionApp> All { get; } = new List<ActionApp>
    {
        App(EverywhereId, "Everywhere", Array.Empty<string>(),
            A("vol-up", "Volume up", "VolumeUp"),
            A("vol-down", "Volume down", "VolumeDown"),
            A("mute", "Mute", "VolumeMute"),
            A("play-pause", "Play / pause", "MediaPlayPause"),
            A("next-track", "Next track", "MediaNext"),
            A("prev-track", "Previous track", "MediaPrev"),
            A("show-desktop", "Show desktop", "Win+D"),
            A("last-window", "Last window", "Alt+Tab"),
            A("task-view", "Task view", "Win+Tab"),
            A("maximize", "Maximize window", "Win+Up"),
            A("snap-left", "Snap left", "Win+Left"),
            A("snap-right", "Snap right", "Win+Right"),
            A("screenshot", "Save a screenshot", "Win+PrintScreen"),
            A("enter", "Enter", "Enter"),
            A("escape", "Escape", "Esc"),
            A("close-window", "Close window", "Alt+F4", danger: true),
            // The D-pad as arrow keys on the desktop: menus, lists, a video's seek. Bound but off
            // the wheel, where four arrow spokes would say nothing.
            A("arrow-up", "Arrow up", "Up", "Up", hidden: true),
            A("arrow-down", "Arrow down", "Down", "Down", hidden: true),
            A("arrow-left", "Arrow left", "Left", "Left", hidden: true),
            A("arrow-right", "Arrow right", "Right", "Right", hidden: true),
            A("copy", "Copy", "Ctrl+C", hidden: true),
            A("paste", "Paste", "Ctrl+V", hidden: true),
            A("undo", "Undo", "Ctrl+Z", hidden: true),
            A("select-all", "Select all", "Ctrl+A", hidden: true)),

        App("firefox", "Firefox", new[] { "firefox" }, Browser("Ctrl+Shift+Y", "Ctrl+Shift+P")),
        App("chrome", "Google Chrome", new[] { "chrome" }, Browser("Ctrl+J", "Ctrl+Shift+N")),
        App("msedge", "Microsoft Edge", new[] { "msedge" }, Browser("Ctrl+J", "Ctrl+Shift+N")),
        App("brave", "Brave", new[] { "brave" }, Browser("Ctrl+J", "Ctrl+Shift+N")),
        App("opera", "Opera", new[] { "opera" }, Browser("Ctrl+J", "Ctrl+Shift+N")),
        App("vivaldi", "Vivaldi", new[] { "vivaldi" }, Browser("Ctrl+J", "Ctrl+Shift+N")),

        App("vlc", "VLC", new[] { "vlc" },
            A("play-pause", "Play / pause", "Space", "X"),
            A("fullscreen", "Full screen", "F", "Y"),
            A("mute", "Mute", "M"),
            A("skip-fwd", "Skip forward 10 s", "Alt+Right"),
            A("skip-back", "Skip back 10 s", "Alt+Left"),
            A("next", "Next", "N"),
            A("prev", "Previous", "P"),
            A("subtitles", "Subtitle track", "V"),
            A("audio", "Audio track", "B"),
            A("faster", "Faster", "]"),
            A("slower", "Slower", "["),
            A("stop", "Stop", "S"),
            A("playlist", "Playlist", "Ctrl+L"),
            A("vol-up", "Volume up", "Ctrl+Up", hidden: true),
            A("vol-down", "Volume down", "Ctrl+Down", hidden: true),
            A("speed-normal", "Normal speed", "=", hidden: true),
            A("open", "Open file", "Ctrl+O", hidden: true),
            A("quit", "Quit VLC", "Ctrl+Q", hidden: true, danger: true)),

        App("spotify", "Spotify", new[] { "spotify" },
            A("play-pause", "Play / pause", "Space", "X"),
            A("next", "Next track", "Ctrl+Right", "Y"),
            A("prev", "Previous track", "Ctrl+Left"),
            A("vol-up", "Volume up", "Ctrl+Up"),
            A("vol-down", "Volume down", "Ctrl+Down"),
            A("mute", "Mute", "Ctrl+Shift+Down"),
            A("shuffle", "Shuffle", "Ctrl+S"),
            A("repeat", "Repeat", "Ctrl+R"),
            A("search", "Search", "Ctrl+L"),
            A("like", "Like this song", "Alt+Shift+B"),
            A("home", "Home", "Alt+Shift+H", hidden: true),
            A("back", "Back", "Alt+Left", hidden: true),
            A("forward", "Forward", "Alt+Right", hidden: true)),

        App("discord", "Discord", new[] { "discord" },
            A("mute", "Mute microphone", "Ctrl+Shift+M", "X"),
            A("deafen", "Deafen", "Ctrl+Shift+D", "Y"),
            A("back", "Back", "Alt+Left"),
            A("forward", "Forward", "Alt+Right"),
            A("next-channel", "Next channel", "Alt+Down"),
            A("prev-channel", "Previous channel", "Alt+Up"),
            A("next-unread", "Next unread", "Alt+Shift+Down"),
            A("prev-unread", "Previous unread", "Alt+Shift+Up"),
            A("search", "Search", "Ctrl+K"),
            A("mark-read", "Mark channel read", "Esc"),
            A("upload", "Upload a file", "Ctrl+Shift+U", hidden: true),
            A("emoji", "Emoji picker", "Ctrl+E", hidden: true),
            A("pins", "Pinned messages", "Ctrl+P", hidden: true)),

        App("explorer", "File Explorer", new[] { "explorer" },
            A("back", "Back", "Alt+Left", "X"),
            A("forward", "Forward", "Alt+Right"),
            A("up", "Up a folder", "Alt+Up", "Y"),
            A("open", "Open", "Enter"),
            A("new-folder", "New folder", "Ctrl+Shift+N"),
            A("rename", "Rename", "F2"),
            A("delete", "Delete", "Delete"),
            A("copy", "Copy", "Ctrl+C"),
            A("paste", "Paste", "Ctrl+V"),
            A("select-all", "Select all", "Ctrl+A"),
            A("address", "Address bar", "Alt+D"),
            A("search", "Search", "Ctrl+E"),
            A("refresh", "Refresh", "F5"),
            A("cut", "Cut", "Ctrl+X", hidden: true),
            A("new-window", "New window", "Ctrl+N", hidden: true),
            A("properties", "Properties", "Alt+Enter", hidden: true),
            A("close", "Close window", "Ctrl+W", hidden: true, danger: true)),

        App("retroarch", "RetroArch", new[] { "retroarch" },
            A("menu", "Menu", "F1", "Y"),
            A("save-state", "Save state", "F2", "X"),
            A("load-state", "Load state", "F4"),
            A("slot-next", "Next state slot", "F7"),
            A("slot-prev", "Previous state slot", "F6"),
            A("screenshot", "Screenshot", "F8"),
            A("fast-forward", "Fast forward", "Space"),
            A("rewind", "Rewind", "R"),
            A("pause", "Pause", "P"),
            A("fullscreen", "Full screen", "F"),
            A("mute", "Mute", "F9"),
            A("reset", "Reset game", "H", hidden: true, danger: true),
            A("quit", "Quit RetroArch", "Esc", hidden: true, danger: true)),

        App("pcsx2", "PCSX2", new[] { "pcsx2-qt", "pcsx2-qtx64", "pcsx2-qtx64-avx2", "pcsx2" },
            A("save-state", "Save state", "F1", "X"),
            A("load-state", "Load state", "F3", "Y"),
            A("slot-next", "Next state slot", "F2"),
            A("slot-prev", "Previous state slot", "Shift+F2"),
            A("pause", "Pause menu", "Esc"),
            A("screenshot", "Screenshot", "F8"),
            A("fullscreen", "Full screen", "Alt+Enter"),
            A("frame-limit", "Frame limiter", "F4"),
            A("turbo", "Turbo", "Tab")),

        App("dolphin", "Dolphin", new[] { "dolphin" },
            A("play-pause", "Play / pause", "F10", "X"),
            A("save-1", "Save state 1", "Shift+F1", "Y"),
            A("load-1", "Load state 1", "F1"),
            A("save-2", "Save state 2", "Shift+F2"),
            A("load-2", "Load state 2", "F2"),
            A("save-3", "Save state 3", "Shift+F3"),
            A("load-3", "Load state 3", "F3"),
            A("screenshot", "Screenshot", "F9"),
            A("fullscreen", "Full screen", "Alt+Enter"),
            A("stop", "Stop", "Esc", danger: true)),
    };

    public static ActionApp? Find(string id) => All.FirstOrDefault(a => a.Id == id);

    /// <summary>The pack an exe name belongs to, if any. "chrome" → Google Chrome.</summary>
    public static ActionApp? ForExe(string exe) =>
        All.FirstOrDefault(a => a.Exes.Contains(exe, StringComparer.OrdinalIgnoreCase));
}

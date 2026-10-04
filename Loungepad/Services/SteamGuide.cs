using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Loungepad.Services;

/// <summary>
/// Steam's use of the Xbox button, for the signed-in account: two keys at the top level of
/// userdata\&lt;account id&gt;\config\localconfig.vdf. The names are Steam's own, matched to the
/// settings they drive in SteamUI.dll's settings table (Sept 2026):
///
///  • Controller_CheckGuideButton ↔ controller_guide_button_focus_steam, "Guide Button Focuses
///    Steam": the Xbox button brings Steam or Big Picture forward.
///  • SteamController_Enable_Chord ↔ controller_enable_chord: Steam's Guide button chords
///    (Guide + a button for the keyboard, a screenshot, Big Picture and so on).
///
/// Both are on when absent. One switch here drives both, since either one takes the button --
/// and, since Oct 2026, Steam's desktop layout as well (SteamDesktopLayout): off empties it for
/// every kind of pad, on puts it back, and Read() reports "on" while either half still has the pad.
///
/// Steam rewrites localconfig.vdf when it exits, so an edit made while it runs is lost. Changing
/// it therefore closes Steam (steam.exe -shutdown, which is a clean exit), edits the file, and
/// starts it again tray-only (-silent) if it had been running. A copy of the file is kept beside
/// it as localconfig.vdf.loungepad-bak before every write.
/// </summary>
internal static class SteamGuide
{
    public const string FocusKey = "Controller_CheckGuideButton";
    public const string ChordKey = "SteamController_Enable_Chord";
    internal const long SteamId64Base = 76561197960265728;

    private static (string Path, DateTime Stamp, bool On)? _cache;

    /// <summary>The signed-in account's localconfig.vdf, or null with no Steam or no account.</summary>
    public static string? ConfigPath()
    {
        var steam = SteamAccountService.SteamPath();
        var account = SteamAccountService.DetectAccount();
        if (steam is null || account is null || !long.TryParse(account.SteamId, out var id64)) return null;
        var path = Path.Combine(steam, "userdata", (id64 - SteamId64Base).ToString(), "config", "localconfig.vdf");
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Whether Steam takes the pad: the Xbox button (the two keys), or the desktop, through a
    /// desktop layout that is not empty for every kind of pad (SteamDesktopLayout). Null when
    /// there is no Steam config to read. The keys are cached on the file's write time, since this
    /// is asked on every state push; the layouts are a dozen small files and are read as they are.
    /// </summary>
    public static bool? Read()
    {
        try
        {
            var path = ConfigPath();
            if (path is null) return null;
            var stamp = File.GetLastWriteTimeUtc(path);
            bool on;
            if (_cache is { } c && c.Path == path && c.Stamp == stamp) on = c.On;
            else
            {
                var values = ReadTopLevel(File.ReadAllText(path), FocusKey, ChordKey);
                on = IsOn(values, FocusKey) || IsOn(values, ChordKey);
                _cache = (path, stamp, on);
            }
            return on || SteamDesktopLayout.AllEmpty() == false;
        }
        catch (Exception ex)
        {
            Log.Info($"Steam: could not read the Guide button settings: {ex.Message}");
            return null;
        }
    }

    private static bool IsOn(IReadOnlyDictionary<string, string> values, string key) =>
        !values.TryGetValue(key, out var v) || v != "0";

    public static bool IsRunning()
    {
        var ps = Process.GetProcessesByName("steam");
        foreach (var p in ps) p.Dispose();
        return ps.Length > 0;
    }

    /// <summary>
    /// Switch it, closing and reopening Steam around the edit when it is running. Blocks for as
    /// long as Steam takes to close (up to 30 s), so it runs off the UI thread. Null on success,
    /// otherwise what to tell the user.
    /// </summary>
    public static string? Set(bool on)
    {
        var path = ConfigPath();
        if (path is null) return "Steam is not set up on this PC";
        var exe = SteamExe();
        bool wasRunning = IsRunning();
        if (wasRunning)
        {
            if (exe is null) return "Could not find steam.exe to close Steam";
            Log.Info("Steam: closing it to change the Guide button settings");
            try { Process.Start(new ProcessStartInfo(exe, "-shutdown") { UseShellExecute = false })?.Dispose(); }
            catch (Exception ex) { return $"Could not close Steam: {ex.Message}"; }
            var until = DateTime.UtcNow.AddSeconds(30);
            while (IsRunning())
            {
                if (DateTime.UtcNow > until) return "Steam did not close, so nothing was changed";
                Thread.Sleep(250);
            }
            Thread.Sleep(500);   // the last file handles go a moment after the process
        }

        string? error = null;
        try
        {
            var text = File.ReadAllText(path);
            var value = on ? "1" : "0";
            var edited = SetTopLevel(text, new Dictionary<string, string> { [FocusKey] = value, [ChordKey] = value });
            if (edited is null) error = "Steam's settings file is not in a shape Loungepad can edit";
            else if (edited != text)
            {
                File.Copy(path, path + ".loungepad-bak", overwrite: true);
                var temp = path + ".loungepad-tmp";
                File.WriteAllText(temp, edited);
                File.Move(temp, path, overwrite: true);
                _cache = null;
                Log.Info($"Steam: Guide Button Focuses Steam and Guide chords {(on ? "on" : "off")}");
            }
        }
        catch (Exception ex)
        {
            error = $"Could not change Steam's settings: {ex.Message}";
            Log.Info($"Steam: {error}");
        }

        // The same switch covers the desktop: off empties Steam's desktop layout for every kind of
        // pad, on puts back whatever was there. Done here because Steam is closed here, and Steam
        // rewrites the layout's selection file when it exits.
        try { Log.Info(SteamDesktopLayout.Set(steamKeepsDesktop: on)); }
        catch (Exception ex)
        {
            error ??= $"Could not change Steam's desktop layout: {ex.Message}";
            Log.Info($"Steam: desktop layout: {ex.Message}");
        }

        if (wasRunning && exe is not null)
        {
            try { Process.Start(new ProcessStartInfo(exe, "-silent") { UseShellExecute = false })?.Dispose(); }
            catch (Exception ex) { Log.Info($"Steam: could not start it again: {ex.Message}"); }
        }
        return error;
    }

    private static string? SteamExe()
    {
        var exe = (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamExe", null) as string)?.Replace('/', '\\');
        if (exe is not null && File.Exists(exe)) return exe;
        var dir = SteamAccountService.SteamPath();
        exe = dir is null ? null : Path.Combine(dir, "steam.exe");
        return exe is not null && File.Exists(exe) ? exe : null;
    }

    // ---- the file: Valve's text KeyValues, one key or brace per line ----

    private static readonly Regex ValueLine = new("^(\\s*)\"([^\"]*)\"(\\s+)\"((?:[^\"\\\\]|\\\\.)*)\"\\s*$");

    /// <summary>The values of these keys directly inside the root block.</summary>
    public static Dictionary<string, string> ReadTopLevel(string text, params string[] keys)
    {
        var found = new Dictionary<string, string>();
        int depth = 0;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var t = line.Trim();
            if (t == "{") { depth++; continue; }
            if (t == "}") { depth--; continue; }
            if (depth != 1) continue;
            var m = ValueLine.Match(line);
            if (m.Success && keys.Contains(m.Groups[2].Value)) found[m.Groups[2].Value] = m.Groups[4].Value;
        }
        return found;
    }

    /// <summary>
    /// The file with these keys set directly inside the root block: a value line that is there is
    /// changed in place, one that is not is added before the root's closing brace in Steam's own
    /// layout. Every other line is left byte for byte. Null when the file has no root block.
    /// </summary>
    public static string? SetTopLevel(string text, IReadOnlyDictionary<string, string> values)
    {
        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var pending = new Dictionary<string, string>(values);
        int depth = 0, rootClose = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            var t = lines[i].Trim();
            if (t == "{") { depth++; continue; }
            if (t == "}")
            {
                depth--;
                if (depth == 0 && rootClose < 0) rootClose = i;
                continue;
            }
            if (depth != 1) continue;
            var m = ValueLine.Match(lines[i]);
            if (!m.Success || !pending.TryGetValue(m.Groups[2].Value, out var v)) continue;
            lines[i] = $"{m.Groups[1].Value}\"{m.Groups[2].Value}\"{m.Groups[3].Value}\"{v}\"";
            pending.Remove(m.Groups[2].Value);
        }
        if (rootClose < 0) return null;
        foreach (var (k, v) in pending) lines.Insert(rootClose++, $"\t\"{k}\"\t\t\"{v}\"");
        return string.Join(nl, lines);
    }
}

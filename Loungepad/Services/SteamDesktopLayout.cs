using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Loungepad.Services;

/// <summary>
/// Steam's desktop layout, emptied for every kind of pad -- or put back -- alongside the Guide
/// button switch in SteamGuide. One switch: "Steam on the Xbox button" off means Steam leaves the
/// pad alone on the desktop as well.
///
/// With Steam running and a pad it has configuration support for, Steam Input applies a "desktop
/// layout" whenever no game is in front. Its default (controller_base\desktop_*.vdf) turns the
/// LEFT STICK and the D-pad into the arrow keys, the right stick into the mouse, A into Enter and
/// the triggers into clicks: everything Loungepad does with the pad on the desktop, done a second
/// time by Steam, so a stick that should only move the pointer walks a menu's highlight too. The
/// way out is the layout editor started from controller_base\empty.vdf, which Steam autosaves as
///   steamapps\common\Steam Controller Configs\&lt;account id&gt;\config\413080\controller_&lt;type&gt;.vdf
/// (413080 is the Desktop pseudo-app) and selects through
///   ...\config\configset_controller_&lt;type&gt;.vdf  →  "413080" { "autosave" "1" }.
/// This writes that layout for every controller type Steam ships a desktop template for, and keeps
/// whatever was there beside it as .loungepad-bak (zero bytes when there was nothing), so it can
/// all be put back when Steam is given the pad again. Only ever called with Steam closed
/// (SteamGuide.Set): Steam rewrites the configsets when it exits.
///
/// The layout is the shape Steam autosaved for this PC's Xbox pad (Oct 2026): the switches group
/// keeping the Share/Capture button as a Steam screenshot, and the two trigger groups Steam adds
/// by itself. Steam applied that one Xbox file to a DualSense as well, so one file may be enough
/// on some installs; a file per type is what the folder's naming asks for and costs nothing.
/// </summary>
internal static class SteamDesktopLayout
{
    public const string DesktopAppId = "413080";
    /// <summary>The layout's title, which Steam shows as its name, and how our files are known.</summary>
    public const string Title = "Loungepad";
    public const string BakSuffix = ".loungepad-bak";

    /// <summary>Every type Steam has a desktop template for under controller_base, bar the Steam
    /// Controller and the Steam Deck, whose desktop layout is the point of the hardware.</summary>
    public static readonly string[] Types =
    {
        "controller_xbox360", "controller_xboxone", "controller_ps4", "controller_ps5",
        "controller_switch_pro", "controller_generic",
    };

    /// <summary>The signed-in account's controller config folder, or null with no Steam or no account.</summary>
    public static string? ConfigDir()
    {
        var steam = SteamAccountService.SteamPath();
        var account = SteamAccountService.DetectAccount();
        if (steam is null || account is null || !long.TryParse(account.SteamId, out var id64)) return null;
        var dir = Path.Combine(steam, "steamapps", "common", "Steam Controller Configs",
            (id64 - SteamGuide.SteamId64Base).ToString(), "config");
        return Directory.Exists(dir) ? dir : null;
    }

    private static string LayoutPath(string dir, string type) => Path.Combine(dir, DesktopAppId, type + ".vdf");
    private static string ConfigsetPath(string dir, string type) => Path.Combine(dir, "configset_" + type + ".vdf");

    /// <summary>
    /// True when every type has an empty desktop layout selected -- ours, or one the user started
    /// from empty.vdf -- so Steam types nothing for the pad on the desktop; false when Steam's own
    /// layout would apply to at least one kind of pad; null when there is no Steam config to read.
    /// </summary>
    public static bool? AllEmpty(string? dir = null)
    {
        dir ??= ConfigDir();
        if (dir is null) return null;
        try
        {
            foreach (var type in Types)
            {
                var layout = LayoutPath(dir, type);
                if (!File.Exists(layout) || !IsEmptyLayout(File.ReadAllText(layout))) return false;
                var configset = ConfigsetPath(dir, type);
                if (!File.Exists(configset) || !SelectsAutosave(File.ReadAllText(configset))) return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Info($"Steam: could not read the desktop layouts: {ex.Message}");
            return null;
        }
    }

    private static readonly Regex TopValue = new("^\\s*\"(title|progenitor)\"\\s+\"([^\"]*)\"\\s*$", RegexOptions.Multiline);

    /// <summary>A layout that binds nothing: ours by title, or anything grown from Steam's empty.vdf.</summary>
    public static bool IsEmptyLayout(string text)
    {
        foreach (Match m in TopValue.Matches(text))
        {
            if (m.Groups[1].Value == "title" && m.Groups[2].Value == Title) return true;
            if (m.Groups[1].Value == "progenitor" && m.Groups[2].Value.EndsWith("/empty.vdf", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// Write the empty layouts (<paramref name="steamKeepsDesktop"/> false) or put back what was
    /// there (true). Returns a line for the log; throws when a file cannot be written, which the
    /// caller turns into the row's message.
    /// </summary>
    public static string Set(bool steamKeepsDesktop, string? dir = null)
    {
        dir ??= ConfigDir();
        if (dir is null) return "Steam desktop layout: no Steam config folder, nothing changed";
        return steamKeepsDesktop ? Restore(dir) : Empty(dir);
    }

    private static string Empty(string dir)
    {
        var creator = SteamAccountService.DetectAccount()?.SteamId ?? "0";
        Directory.CreateDirectory(Path.Combine(dir, DesktopAppId));
        int written = 0, kept = 0;
        foreach (var type in Types)
        {
            var layout = LayoutPath(dir, type);
            // The first write keeps the original; a second Off must not replace the backup with ours.
            if (KeepBackup(layout)) kept++;
            WriteAtomic(layout, LayoutText(type, creator));
            written++;

            var configset = ConfigsetPath(dir, type);
            var text = File.Exists(configset) ? File.ReadAllText(configset) : null;
            var edited = text is null ? NewConfigset() : SelectAutosave(text);
            if (edited is null || edited == text) continue;   // already selects the autosave, or not a shape to edit
            KeepBackup(configset);
            WriteAtomic(configset, edited);
        }
        return $"Steam desktop layout: emptied for {written} pad types ({kept} previous layouts kept as {BakSuffix})";
    }

    private static string Restore(string dir)
    {
        int restored = 0, removed = 0;
        foreach (var type in Types)
        {
            foreach (var path in new[] { LayoutPath(dir, type), ConfigsetPath(dir, type) })
            {
                var bak = path + BakSuffix;
                if (!File.Exists(bak)) continue;    // never ours to begin with: left alone
                if (new FileInfo(bak).Length == 0)
                {
                    // Zero bytes: there was no file before Loungepad wrote one.
                    File.Delete(path);
                    File.Delete(bak);
                    removed++;
                }
                else
                {
                    File.Move(bak, path, overwrite: true);
                    restored++;
                }
            }
        }
        return $"Steam desktop layout: put back ({restored} restored, {removed} removed)";
    }

    /// <summary>Copy the file beside itself as the backup, or leave a zero-byte marker when there is
    /// no file, unless a backup is already there. True when an existing file was kept.</summary>
    private static bool KeepBackup(string path)
    {
        var bak = path + BakSuffix;
        if (File.Exists(bak)) return false;
        if (File.Exists(path)) { File.Copy(path, bak); return true; }
        File.WriteAllBytes(bak, Array.Empty<byte>());
        return false;
    }

    private static void WriteAtomic(string path, string text)
    {
        var temp = path + ".loungepad-tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, path, overwrite: true);
    }

    // ---- the files ----

    /// <summary>The empty desktop layout for one controller type, in Steam's autosave shape.</summary>
    public static string LayoutText(string type, string creator)
    {
        var sb = new StringBuilder();
        void L(string s) => sb.Append(s).Append('\n');
        L("\"controller_mappings\"");
        L("{");
        L("\t\"version\"\t\t\"3\"");
        L("\t\"revision\"\t\t\"1\"");
        L($"\t\"title\"\t\t\"{Title}\"");
        L("\t\"description\"\t\t\"Nothing bound on the desktop: Loungepad drives the pointer. Settings → Controller → Windows and Steam.\"");
        L($"\t\"creator\"\t\t\"{creator}\"");
        L("\t\"progenitor\"\t\t\"local://controller_base/empty.vdf\"");
        L("\t\"url\"\t\t\"local://controller_base/empty.vdf\"");
        L("\t\"export_type\"\t\t\"unknown\"");
        L($"\t\"controller_type\"\t\t\"{type}\"");
        L("\t\"major_revision\"\t\t\"0\"");
        L("\t\"minor_revision\"\t\t\"0\"");
        // The one binding kept: the Share/Capture button as a Steam screenshot, which is what the
        // user's own emptied layout kept for the Xbox pad.
        L("\t\"group\"");
        L("\t{");
        L("\t\t\"id\"\t\t\"0\"");
        L("\t\t\"mode\"\t\t\"switches\"");
        L("\t\t\"name\"\t\t\"\"");
        L("\t\t\"description\"\t\t\"\"");
        L("\t\t\"inputs\"");
        L("\t\t{");
        L("\t\t\t\"button_capture\"");
        L("\t\t\t{");
        L("\t\t\t\t\"activators\"");
        L("\t\t\t\t{");
        L("\t\t\t\t\t\"Full_Press\"");
        L("\t\t\t\t\t{");
        L("\t\t\t\t\t\t\"bindings\"");
        L("\t\t\t\t\t\t{");
        L("\t\t\t\t\t\t\t\"binding\"\t\t\"controller_action SCREENSHOT, , \"");
        L("\t\t\t\t\t\t}");
        L("\t\t\t\t\t}");
        L("\t\t\t\t}");
        L("\t\t\t\t\"disabled_activators\"");
        L("\t\t\t\t{");
        L("\t\t\t\t}");
        L("\t\t\t}");
        L("\t\t}");
        L("\t}");
        foreach (var id in new[] { "1", "2" })
        {
            L("\t\"group\"");
            L("\t{");
            L($"\t\t\"id\"\t\t\"{id}\"");
            L("\t\t\"mode\"\t\t\"trigger\"");
            L("\t\t\"name\"\t\t\"\"");
            L("\t\t\"description\"\t\t\"\"");
            L("\t\t\"inputs\"");
            L("\t\t{");
            L("\t\t}");
            L("\t}");
        }
        L("\t\"preset\"");
        L("\t{");
        L("\t\t\"id\"\t\t\"0\"");
        L("\t\t\"name\"\t\t\"Default\"");
        L("\t\t\"group_source_bindings\"");
        L("\t\t{");
        L("\t\t\t\"0\"\t\t\"switch active\"");
        L("\t\t\t\"1\"\t\t\"left_trigger active\"");
        L("\t\t\t\"2\"\t\t\"right_trigger active\"");
        L("\t\t}");
        L("\t}");
        L("\t\"settings\"");
        L("\t{");
        L("\t}");
        L("}");
        return sb.ToString();
    }

    private static string NewConfigset() =>
        "\"controller_config\"\n{\n\t\"" + DesktopAppId + "\"\n\t{\n\t\t\"autosave\"\t\t\"1\"\n\t}\n}\n";

    private static readonly Regex KeyLine = new("^\\s*\"([^\"]*)\"\\s*$");
    private static readonly Regex ValueLine = new("^\\s*\"([^\"]*)\"\\s+\"([^\"]*)\"\\s*$");

    /// <summary>Does the configset point the desktop at the autosaved layout?</summary>
    public static bool SelectsAutosave(string text)
    {
        var (start, end, lines) = FindBlock(text);
        if (start < 0) return false;
        for (int i = start + 2; i < end; i++)
        {
            var m = ValueLine.Match(lines[i]);
            if (m.Success && m.Groups[1].Value == "autosave") return m.Groups[2].Value == "1";
        }
        return false;
    }

    /// <summary>
    /// The configset with its desktop entry set to the autosave: an existing "413080" block is
    /// replaced whatever it chose (a template, a workshop layout), a missing one is added before
    /// the root's closing brace. Every other line is left as it was. Null when the file has no
    /// root block.
    /// </summary>
    public static string? SelectAutosave(string text)
    {
        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var (start, end, lines) = FindBlock(text);
        var block = new[] { $"\t\"{DesktopAppId}\"", "\t{", "\t\t\"autosave\"\t\t\"1\"", "\t}" };
        if (start >= 0)
        {
            lines.RemoveRange(start, end - start + 1);
            lines.InsertRange(start, block);
        }
        else
        {
            int depth = 0, rootClose = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                var t = lines[i].Trim();
                if (t == "{") depth++;
                else if (t == "}" && --depth == 0) { rootClose = i; break; }
            }
            if (rootClose < 0) return null;
            lines.InsertRange(rootClose, block);
        }
        return string.Join(nl, lines);
    }

    /// <summary>The line range of the depth-1 "413080" block (its key line to its closing brace), or -1.</summary>
    private static (int Start, int End, List<string> Lines) FindBlock(string text)
    {
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        int depth = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            var t = lines[i].Trim();
            if (t == "{") { depth++; continue; }
            if (t == "}") { depth--; continue; }
            if (depth != 1) continue;
            var m = KeyLine.Match(lines[i]);
            if (!m.Success || m.Groups[1].Value != DesktopAppId) continue;
            // The block: the next "{" and the "}" that closes it.
            int j = i + 1;
            if (j >= lines.Count || lines[j].Trim() != "{") return (-1, -1, lines);
            int d = 0;
            for (; j < lines.Count; j++)
            {
                var u = lines[j].Trim();
                if (u == "{") d++;
                else if (u == "}" && --d == 0) return (i, j, lines);
            }
            return (-1, -1, lines);
        }
        return (-1, -1, lines);
    }
}

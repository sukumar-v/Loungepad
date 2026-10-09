using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Loungepad.Interop;
using Microsoft.Win32;

namespace Loungepad.Services;

/// <summary>
/// Is Steam's Big Picture in front, or a game Steam started?
///
/// Both read the controller themselves, so with either in front Loungepad's desktop mapping (the
/// stick as a mouse, A as a click, the D-pad as arrow keys) was a second driver on every press:
/// focus stepped twice, A clicked wherever the pointer had been left, and the pointer wandered
/// over a screen that was being navigated by focus. The pad service treats both like a focused
/// game Loungepad launched itself (MainWindow's isGameFocused), behind the SteamOwnsPad switch.
///
/// Big Picture is a window of steamwebhelper.exe titled in Steam's own language. The title is
/// SP_WindowTitle_BigPicture in steamui\localization\steamui_&lt;language&gt;-json.js and is read
/// from there: "Steam Big Picture Mode" in English, "Steam 大屏幕模式" in Simplified Chinese, so
/// no fixed string would do. A game is RunningAppID in HKCU\Software\Valve\Steam with the
/// foreground process under a steamapps\common folder, whether it was started from Big Picture or
/// from Steam's desktop window.
///
/// Asked from the pad's thread on every poll, so the answer is kept per foreground window for
/// half a second; the window that asks from the UI thread (KeepFocus) shares it under the lock.
/// </summary>
internal sealed class SteamForeground
{
    public const string BigPicture = "Big Picture";
    public const string SteamGame = "a Steam game";
    private const string EnglishTitle = "Steam Big Picture Mode";
    /// <summary>"Never checked" for the stamps below. Not long.MinValue: now minus that overflows
    /// to a negative age, which read as fresh, so nothing was ever checked at all.</summary>
    private const long Never = long.MinValue / 2;

    private readonly object _gate = new();
    private IntPtr _hwnd;
    private long _checkedAt = Never;
    private string? _owner;
    private string? _language, _title;
    private long _titleAt = Never;
    private long _appAt = Never;
    private int _runningApp;

    /// <summary>The app id Steam says is running, 0 for none. Replaceable for the harness, which
    /// must not write Steam's own registry values.</summary>
    internal Func<int> ReadRunningApp = ReadRunningAppFromRegistry;

    public SteamForeground()
    {
        // The localization file is most of a megabyte: read it off the pad's thread.
        Task.Run(() => { lock (_gate) Title(Environment.TickCount64); });
    }

    /// <summary>What has the controller now: <see cref="BigPicture"/>, <see cref="SteamGame"/>,
    /// or null for anything else.</summary>
    public string? Owner()
    {
        lock (_gate)
        {
            var hwnd = NativeMethods.GetForegroundWindow();
            long now = Environment.TickCount64;
            if (hwnd == _hwnd && now - _checkedAt < 500) return _owner;
            _hwnd = hwnd;
            _checkedAt = now;
            string? owner;
            try { owner = Check(hwnd, now); }
            catch (Exception) { owner = null; }   // a window or process gone mid-question
            if (owner != _owner)
                Log.Info(owner is not null ? $"Steam: {owner} is in front" : $"Steam: {_owner} is no longer in front");
            _owner = owner;
            return owner;
        }
    }

    private string? Check(IntPtr hwnd, long now)
    {
        if (hwnd == IntPtr.Zero) return null;
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        var path = ProcessPath(pid);
        if (path is null) return null;
        var exe = Path.GetFileName(path);
        if (exe.Equals("steamwebhelper.exe", StringComparison.OrdinalIgnoreCase) || exe.Equals("steam.exe", StringComparison.OrdinalIgnoreCase))
            return IsBigPictureTitle(WindowTitle(hwnd), now) ? BigPicture : null;
        if (path.IndexOf(@"\steamapps\common\", StringComparison.OrdinalIgnoreCase) < 0) return null;
        return RunningApp(now) != 0 ? SteamGame : null;
    }

    private bool IsBigPictureTitle(string text, long now)
    {
        text = text.Trim();
        if (text.Length == 0) return false;
        var title = Title(now)?.Trim();
        return (title is { Length: > 0 } && text.Equals(title, StringComparison.Ordinal))
            || text.Equals(EnglishTitle, StringComparison.Ordinal)
            // A language whose file could not be read: most of them still say Big Picture.
            || text.Contains("Big Picture", StringComparison.OrdinalIgnoreCase);
    }

    private int RunningApp(long now)
    {
        if (now - _appAt < 1000) return _runningApp;
        _appAt = now;
        try { _runningApp = ReadRunningApp(); }
        catch (Exception) { _runningApp = 0; }
        return _runningApp;
    }

    private static int ReadRunningAppFromRegistry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        return key?.GetValue("RunningAppID") is int id ? id : 0;
    }

    /// <summary>Big Picture's window title in the language Steam runs in, re-checked once a minute
    /// (the language only changes with a Steam restart, and the file is only read again then).</summary>
    private string? Title(long now)
    {
        if (now - _titleAt < 60_000) return _title;
        _titleAt = now;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var language = key?.GetValue("Language") as string;
            if (string.IsNullOrWhiteSpace(language)) language = "english";
            if (language == _language && _title is not null) return _title;
            var steam = key?.GetValue("SteamPath") as string;
            _language = language;
            _title = ReadTitle(steam, language) ?? (language != "english" ? ReadTitle(steam, "english") : null);
        }
        catch (Exception) { /* Steam not installed, or a file mid-update: the fixed checks still run */ }
        return _title;
    }

    private static string? ReadTitle(string? steamPath, string language)
    {
        if (string.IsNullOrWhiteSpace(steamPath) || !Regex.IsMatch(language, "^[A-Za-z_]+$")) return null;
        var file = Path.Combine(steamPath.Replace('/', '\\'), "steamui", "localization", $"steamui_{language}-json.js");
        if (!File.Exists(file)) return null;
        var m = Regex.Match(File.ReadAllText(file, Encoding.UTF8), "\"SP_WindowTitle_BigPicture\":\"((?:[^\"\\\\]|\\\\.)*)\"");
        if (!m.Success) return null;
        // JSON inside a JavaScript string: an escape arrives with its backslash doubled.
        var raw = m.Groups[1].Value;
        if (raw.Contains('\\'))
        {
            try { raw = Regex.Unescape(raw.Replace(@"\\", @"\")); }
            catch (Exception) { /* keep it as written */ }
        }
        return raw.Length > 0 ? raw : null;
    }

    private static string WindowTitle(IntPtr hwnd)
    {
        int len = NativeMethods.GetWindowTextLength(hwnd);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string? ProcessPath(uint pid)
    {
        if (pid == 0) return null;
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
}

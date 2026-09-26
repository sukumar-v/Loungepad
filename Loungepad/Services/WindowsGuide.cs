using Microsoft.Win32;

namespace Loungepad.Services;

/// <summary>
/// Windows' own uses of the Xbox button, read and switched in the user's own hive (HKCU, no
/// elevation, nothing machine-wide). Settings → Controller → Windows and Steam is the only caller.
///
///  • Game Bar: "Open Xbox Game Bar using this button on a controller" (UseNexusForGameBarEnabled)
///    and "Use View + Menu as Guide button in apps" (GamepadNexusChordEnabled), both DWORDs under
///    HKCU\Software\Microsoft\GameBar, both on when absent. One switch here: both are Game Bar
///    taking pad buttons, and the second is what fights a View + Menu menu combo. The Controller
///    Bar is an option inside the first and has no switch of its own to reach.
///  • Xbox mode: HKCU\Software\Microsoft\Windows\CurrentVersion\GamingConfiguration,
///    GamingHomeApp (string). Absent or an app id is on, "" is off -- verified Sept 2026 by
///    reading what Windows' own Settings toggle wrote. While it is on, a long press of the Xbox
///    button opens Task View. It exists from builds 26100.8328 / 26200.8328 / 28000.2179.
///
/// Turning something back on deletes the value rather than writing a 1, so it goes back to exactly
/// what Windows ships. A running Game Bar or shell may only notice after the next sign-in.
/// </summary>
internal static class WindowsGuide
{
    private const string GameBarKey = @"Software\Microsoft\GameBar";
    private const string GameBarButton = "UseNexusForGameBarEnabled";
    private const string GameBarChord = "GamepadNexusChordEnabled";
    private const string GamingKey = @"Software\Microsoft\Windows\CurrentVersion\GamingConfiguration";
    private const string HomeAppValue = "GamingHomeApp";

    /// <summary>Whether Game Bar takes pad buttons, and whether Xbox mode is on (null on a
    /// Windows that has no Xbox mode).</summary>
    public record State(bool GameBar, bool? XboxMode);

    public static State Read()
    {
        bool gameBar = true;
        bool? xboxMode = null;
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(GameBarKey);
            bool button = k?.GetValue(GameBarButton) is not int b || b != 0;
            bool chord = k?.GetValue(GameBarChord) is not int c || c != 0;
            gameBar = button || chord;
        }
        catch { /* unreadable: say it is on, which only costs a row saying so */ }
        if (XboxModeAvailable())
        {
            xboxMode = true;
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(GamingKey);
                if (k?.GetValue(HomeAppValue) is string app) xboxMode = app.Length > 0;
            }
            catch { /* unreadable: on, as above */ }
        }
        return new State(gameBar, xboxMode);
    }

    public static bool SetGameBar(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(GameBarKey);
            if (on)
            {
                k.DeleteValue(GameBarButton, throwOnMissingValue: false);
                k.DeleteValue(GameBarChord, throwOnMissingValue: false);
            }
            else
            {
                k.SetValue(GameBarButton, 0, RegistryValueKind.DWord);
                k.SetValue(GameBarChord, 0, RegistryValueKind.DWord);
            }
            Log.Info($"Windows: Game Bar on the controller {(on ? "back to Windows' default" : "off")}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Info($"Windows: could not change Game Bar's controller buttons: {ex.Message}");
            return false;
        }
    }

    public static bool SetXboxMode(bool on)
    {
        if (!XboxModeAvailable()) return false;
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(GamingKey);
            if (on) k.DeleteValue(HomeAppValue, throwOnMissingValue: false);
            else k.SetValue(HomeAppValue, "", RegistryValueKind.String);
            Log.Info($"Windows: Xbox mode {(on ? "back to Windows' default" : "off")}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Info($"Windows: could not change Xbox mode: {ex.Message}");
            return false;
        }
    }

    /// <summary>The builds that shipped Xbox mode. Earlier ones have no setting to read, and no
    /// long press to worry about.</summary>
    private static bool XboxModeAvailable()
    {
        int build = Environment.OSVersion.Version.Build;
        int ubr = 0;
        try { ubr = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "UBR", 0) is int u ? u : 0; }
        catch { /* treat as the base build */ }
        return build > 28000
            || (build == 28000 && ubr >= 2179)
            || ((build == 26100 || build == 26200) && ubr >= 8328);
    }
}

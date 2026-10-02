using System.IO;
using Microsoft.Win32;
using Windows.Security.Credentials;

namespace Loungepad.Services;

/// <summary>
/// What the first-run setup asks about the PC before it offers anything: which store launchers are
/// installed, and whether Windows Hello has a PIN to sign in with from the sofa. Every answer is
/// read-only and cheap, and a failure is "do not know", never an error on screen.
/// </summary>
public static class FirstRun
{
    public sealed record LauncherSet(bool Steam, bool Epic, bool Galaxy, bool XboxApp);

    /// <summary>The store clients on this PC. Not the same question as "are there games": a
    /// launcher can be installed with nothing in it, and GOG games install without Galaxy.</summary>
    public static LauncherSet Launchers() => new(
        Steam: SteamAccountService.SteamPath() is { } steam && File.Exists(Path.Combine(steam, "steam.exe")),
        Epic: EpicInstalled(),
        Galaxy: GogAccountClient.GalaxyInstalled(),
        // The Xbox app is a Store package; its per-user data folder exists once it is installed
        // for this account, which is cheaper to ask than the package manager.
        XboxApp: Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packages", "Microsoft.GamingApp_8wekyb3d8bbwe")));

    private static bool EpicInstalled()
    {
        foreach (var root in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
            if (File.Exists(Path.Combine(Environment.GetFolderPath(root), "Epic Games", "Launcher", "Portal", "Binaries", "Win64", "EpicGamesLauncher.exe")))
                return true;
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Epic Games\EpicGamesLauncher")
                          ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Epic Games\EpicGamesLauncher");
            return k?.GetValue("AppDataPath") is string p && Directory.Exists(p);
        }
        catch { return false; }
    }

    /// <summary>
    /// Whether this Windows account has Windows Hello set up, which always means a PIN: face and
    /// fingerprint cannot be enrolled without one. A PIN is what makes the lock screen something a
    /// controller can get past, since the touch keyboard's gamepad layout types the digits. Null
    /// when Windows would not say. KeyCredentialManager reports "supported" only once a PIN exists
    /// -- it is the check Microsoft's own Hello samples make before offering to use it.
    /// </summary>
    public static async Task<bool?> PinSetUpAsync()
    {
        try { return await KeyCredentialManager.IsSupportedAsync(); }
        catch (Exception ex)
        {
            Log.Info($"First run: the Windows Hello check failed: {ex.Message}");
            return null;
        }
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Loungepad.Interop;

namespace Loungepad.Services;

/// <summary>A device Windows lists as able to wake the machine, and whether it is allowed to.
/// Kind is "controller" (a pad, or the Xbox Wireless Adapter), or "bluetooth" (a radio or a
/// Bluetooth HID node, which is the route a Bluetooth pad would take).</summary>
public sealed record WakeDevice(string Name, bool Armed, string Kind);

/// <summary>What sleep and wake look like on this PC, for Settings → General → Rest and sleep.</summary>
public sealed record WakeReport(bool CanSleep, bool ModernStandby, bool SignInOnWake, IReadOnlyList<WakeDevice> Devices, string? LastWake);

/// <summary>
/// What can wake this PC from sleep, and what stands in the way.
///
/// The one thing a program cannot do is make a controller wake a sleeping PC: that is a device
/// asking the bus to wake the machine, and whether it may is a per-device switch ("Allow this
/// device to wake the computer" in Device Manager) that only exists for devices whose driver
/// supports it. On a desktop in S3 sleep that is typically the keyboard, the mouse and the network
/// card, and not the Bluetooth radio, so a Bluetooth pad has no route at all; the Xbox Wireless
/// Adapter and some wired pads do offer the switch. Modern Standby boards keep Bluetooth up and a
/// paired pad usually can. So this reads the two lists powercfg keeps -- devices that could be
/// allowed, devices that are -- and picks out the ones that are a controller's, and the Settings
/// row says exactly which case this PC is, with one elevated press to allow a device that could.
///
/// The other thing in the way is Windows asking for a sign-in after a wake, which lands the pad on
/// a lock screen it cannot drive. That is the active power scheme's CONSOLELOCK; readable by
/// anyone, changed only elevated.
///
/// powercfg is spawned rather than the DevicePower* API it wraps: that API answered
/// ERROR_WMI_INSTANCE_NOT_FOUND from an ordinary process here (Sept 2026) while the tool worked.
/// </summary>
public static class WakeInfo
{
    private static readonly Regex ControllerRx = new(
        @"\b(xbox|xinput|game ?pad|game controller|dualsense|dualshock|wireless controller|joystick|8bitdo|nintendo|pro controller)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BluetoothRx = new(@"\bbluetooth\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Spawns powercfg three times: a hundred milliseconds or so. Off the UI thread.</summary>
    public static WakeReport Read()
    {
        bool canSleep = false, modern = false;
        try { canSleep = NativeMethods.IsPwrSuspendAllowed(); } catch { /* no powrprof: no sleep */ }
        try
        {
            var caps = new byte[76];
            if (NativeMethods.CallNtPowerInformation(NativeMethods.SystemPowerCapabilities, IntPtr.Zero, 0, caps, 76) == 0)
                modern = caps[20] != 0;   // AoAc
        }
        catch { /* treat as S3 */ }

        var armed = new HashSet<string>(Devices("wake_armed"), StringComparer.OrdinalIgnoreCase);
        var devices = new List<WakeDevice>();
        foreach (var name in Devices("wake_programmable"))
        {
            var kind = Classify(name);
            if (kind is not null) devices.Add(new WakeDevice(name, armed.Contains(name), kind));
        }
        return new WakeReport(canSleep, modern, ReadSignInOnWake(), devices, LastWakeSource());
    }

    /// <summary>"controller", "bluetooth", or null for a keyboard, a mouse, a network card and the
    /// rest. "HID-compliant system controller" is a keyboard's power-keys collection, not a pad.</summary>
    public static string? Classify(string name)
    {
        if (ControllerRx.IsMatch(name)) return "controller";
        if (BluetoothRx.IsMatch(name)) return "bluetooth";
        return null;
    }

    private static List<string> Devices(string query)
    {
        var list = new List<string>();
        foreach (var raw in Run("powercfg.exe", $"/devicequery {query}").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.Equals("NONE", StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(line);
        }
        return list;
    }

    /// <summary>What last woke the machine -- a device by its friendly name, else the wake type
    /// Windows reports (a timer, the power button) -- or null when Windows has no record. Spawns
    /// powercfg, so off the UI thread.</summary>
    public static string? LastWakeSource()
    {
        string? type = null, description = null;
        foreach (var raw in Run("powercfg.exe", "/lastwake").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("Friendly Name:", StringComparison.OrdinalIgnoreCase) && line.Length > 14) return line[14..].Trim();
            if (line.StartsWith("Description:", StringComparison.OrdinalIgnoreCase)) description ??= line[12..].Trim();
            if (line.StartsWith("Type:", StringComparison.OrdinalIgnoreCase)) type ??= line[5..].Trim();
        }
        return description ?? type;
    }

    private static string Run(string file, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args)
            {
                UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true,
            });
            if (p is null) return "";
            var text = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return text;
        }
        catch (Exception ex)
        {
            Log.Info($"Wake: {file} {args} failed: {ex.Message}");
            return "";
        }
    }

    /// <summary>Whether Windows asks for a sign-in when the PC wakes (the active scheme's "Require a password on wakeup").</summary>
    public static bool ReadSignInOnWake()
    {
        try
        {
            if (NativeMethods.PowerGetActiveScheme(IntPtr.Zero, out var p) != 0 || p == IntPtr.Zero) return false;
            var scheme = Marshal.PtrToStructure<Guid>(p);
            NativeMethods.LocalFree(p);
            var sub = NativeMethods.NO_SUBGROUP_GUID;
            var setting = NativeMethods.GUID_LOCK_CONSOLE_ON_WAKE;
            return NativeMethods.PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out var v) == 0 && v == 1;
        }
        catch { return false; }
    }

    /// <summary>Allow a device to wake the PC: Device Manager's tick, through an elevated powercfg.
    /// One UAC prompt; false if it was declined or Windows refused the device.</summary>
    public static Task<bool> EnableWakeAsync(string deviceName) =>
        RunElevated("powercfg.exe", $"/deviceenablewake \"{deviceName.Replace("\"", "")}\"");

    /// <summary>Stop (or start) Windows asking for a sign-in after a wake, on both power lines of
    /// the active scheme, through an elevated powercfg. One UAC prompt.</summary>
    public static Task<bool> SetSignInOnWakeAsync(bool on)
    {
        int v = on ? 1 : 0;
        return RunElevated("cmd.exe",
            $"/c powercfg /setacvalueindex SCHEME_CURRENT SUB_NONE CONSOLELOCK {v} && " +
            $"powercfg /setdcvalueindex SCHEME_CURRENT SUB_NONE CONSOLELOCK {v} && " +
            "powercfg /setactive SCHEME_CURRENT");
    }

    private static Task<bool> RunElevated(string file, string args) => Task.Run(() =>
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args)
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (p is null) return false;
            p.WaitForExit(30_000);
            bool ok = p.HasExited && p.ExitCode == 0;
            Log.Info($"Wake: {file} {args} -> {(ok ? "ok" : p.HasExited ? $"exit {p.ExitCode}" : "still running")}");
            return ok;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Log.Info("Wake: the elevation prompt was declined");
            return false;
        }
        catch (Exception ex)
        {
            Log.Info($"Wake: {file} {args} failed: {ex.Message}");
            return false;
        }
    });
}

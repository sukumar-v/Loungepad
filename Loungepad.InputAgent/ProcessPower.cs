using System.Runtime.InteropServices;

namespace Loungepad.InputAgent;

/// <summary>
/// Opts the worker out of Windows' power throttling. Windows 11 may put a process that shows no
/// window on EcoQoS and stop honouring its timer resolution, and a pointer loop that has to run
/// every 8 ms in a process that never shows a window is exactly that process. Per process and
/// documented (SetProcessInformation, ProcessPowerThrottling); nothing system-wide changes. A
/// control bit set with its state bit clear means "never throttle this".
/// </summary>
internal static class ProcessPower
{
    private const int ProcessPowerThrottling = 4;
    private const uint ExecutionSpeed = 0x1, IgnoreTimerResolution = 0x4;

    public static bool KeepResponsive()
    {
        var state = new PROCESS_POWER_THROTTLING_STATE { Version = 1, ControlMask = ExecutionSpeed | IgnoreTimerResolution, StateMask = 0 };
        return SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
    }

    [StructLayout(LayoutKind.Sequential)] private struct PROCESS_POWER_THROTTLING_STATE { public uint Version, ControlMask, StateMask; }
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PROCESS_POWER_THROTTLING_STATE state, int size);
}

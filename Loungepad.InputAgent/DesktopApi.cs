using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Loungepad.InputAgent;

internal sealed class InputDesktop : IDisposable
{
    public IntPtr Handle { get; }
    public string Name { get; }
    public InputDesktop(IntPtr handle, string name) { Handle = handle; Name = name; }
    public void Dispose() => DesktopApi.CloseDesktop(Handle);
}

internal static class DesktopApi
{
    public static InputDesktop Open()
    {
        IntPtr handle = OpenInputDesktop(0, false, 0x1FF); // desktop rights only, no DACL changes
        if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try { return new InputDesktop(handle, ObjectName(handle)); }
        catch { CloseDesktop(handle); throw; }
    }

    private static string ObjectName(IntPtr handle)
    {
        var name = new StringBuilder(256);
        if (!GetUserObjectInformation(handle, 2, name, name.Capacity * sizeof(char), out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return name.ToString();
    }

    public static void VerifyWorkerDesktop(string expected)
    {
        // CreateProcess's lpDesktop binds this fresh process before the CLR enters STA.
        // Calling SetThreadDesktop afterwards can fail with ERROR_BUSY (170), because
        // COM has already created its STA window. Validate the inherited binding instead.
        // GetThreadDesktop returns a borrowed handle; do not close it.
        IntPtr handle = GetThreadDesktop(GetCurrentThreadId());
        if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!string.Equals(ObjectName(handle), expected, StringComparison.Ordinal))
            throw new InvalidOperationException("Worker was launched on the wrong desktop");
    }

    public static bool IsCurrent(string name)
    {
        try { using var desktop = Open(); return desktop.Name == name && Process.GetCurrentProcess().SessionId == WTSGetActiveConsoleSessionId(); }
        catch { return false; }
    }

    public static Process StartWorker(string desktop)
    {
        string exe = Environment.ProcessPath!;
        var startup = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = @"WinSta0\" + desktop,
            dwFlags = 0x80 }; // STARTF_FORCEOFFFEEDBACK: a background worker must not flash the busy cursor
        if (!CreateProcess(exe, new StringBuilder($"\"{exe}\" --desktop {desktop}"), IntPtr.Zero, IntPtr.Zero,
            false, 0x08000000, IntPtr.Zero, AppContext.BaseDirectory, ref startup, out var pi))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try { return Process.GetProcessById((int)pi.dwProcessId); }
        finally { CloseHandle(pi.hProcess); CloseHandle(pi.hThread); }
    }

    public static SecurityIdentifier? ConsoleUser()
    {
        if (!WTSQueryUserToken((uint)Process.GetCurrentProcess().SessionId, out var token)) return null;
        using (token) { using var identity = new WindowsIdentity(token.DangerousGetHandle()); return identity.User; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct STARTUPINFO
    {
        public int cb; public string? lpReserved, lpDesktop, lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public ushort wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public uint dwProcessId, dwThreadId; }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder info, int length, out int needed);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetThreadDesktop(uint threadId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] internal static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("kernel32.dll")] internal static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("kernel32.dll")] internal static extern bool ProcessIdToSessionId(uint pid, out uint session);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSQueryUserToken(uint session, out SafeAccessTokenHandle token);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcess(string application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string directory, ref STARTUPINFO startup, out PROCESS_INFORMATION process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}

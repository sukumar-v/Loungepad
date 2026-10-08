using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Loungepad.Service;

// Session 0 supervises only. All user32/input work belongs to the child in WinSta0.
internal sealed class SessionProcess : IDisposable
{
    private IntPtr _process, _job;
    public bool HasExited => WaitForSingleObject(_process, 0) != 258;

    public static SessionProcess Start(uint session)
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "Agent", "Loungepad.InputAgent.exe");
        if (!File.Exists(exe)) throw new FileNotFoundException("Install the signed input agent in the service's Agent folder", exe);
        Check(OpenProcessToken(Process.GetCurrentProcess().Handle, 0x2B, out var source));
        IntPtr token = IntPtr.Zero;
        var child = new SessionProcess();
        try
        {
            EnablePrivilege(source, "SeTcbPrivilege");
            Check(DuplicateTokenEx(source, 0x02000000, IntPtr.Zero, 2, 1, out token));
            Check(SetTokenInformation(token, 12, ref session, sizeof(uint))); // TokenSessionId
            // CreateProcessAsUser does not ask AppInfo to prepare a UIAccess token.
            // The signed agent's uiAccess=true manifest must match the token supplied here,
            // otherwise Windows rejects startup with ERROR_ELEVATION_REQUIRED (740), even
            // though the service is SYSTEM. Adjust only our own duplicated SYSTEM token.
            uint uiAccess = 1;
            Check(SetTokenInformation(token, 26, ref uiAccess, sizeof(uint))); // TokenUIAccess
            var startup = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = @"WinSta0\Default", dwFlags = 0x80 };
            Check(CreateProcessAsUser(token, exe, new StringBuilder($"\"{exe}\""), IntPtr.Zero, IntPtr.Zero,
                false, 0x08000004, IntPtr.Zero, AppContext.BaseDirectory, ref startup, out var pi));
            child._process = pi.hProcess;
            try
            {
                child._job = CreateJobObject(IntPtr.Zero, null);
                Check(child._job != IntPtr.Zero);
                var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                limits.BasicLimitInformation.LimitFlags = 0x2000; // KILL_ON_JOB_CLOSE, including service crashes
                Check(SetInformationJobObject(child._job, 9, ref limits, (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()));
                Check(AssignProcessToJobObject(child._job, child._process));
                Check(ResumeThread(pi.hThread) != uint.MaxValue);
            }
            finally { CloseHandle(pi.hThread); }
            return child;
        }
        catch { child.Dispose(); throw; }
        finally { if (token != IntPtr.Zero) CloseHandle(token); CloseHandle(source); }
    }

    private static void EnablePrivilege(IntPtr token, string name)
    {
        Check(LookupPrivilegeValue(null, name, out var luid));
        var privileges = new TOKEN_PRIVILEGES { Count = 1, Luid = luid, Attributes = 2 };
        Check(AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero));
        if (Marshal.GetLastWin32Error() != 0) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public void Dispose()
    {
        if (_process != IntPtr.Zero) { TerminateProcess(_process, 0); WaitForSingleObject(_process, 2000); CloseHandle(_process); _process = IntPtr.Zero; }
        if (_job != IntPtr.Zero) { CloseHandle(_job); _job = IntPtr.Zero; }
    }
    private static void Check(bool ok, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(ok))] string? operation = null)
    {
        if (ok) return;
        int error = Marshal.GetLastWin32Error();
        throw new Win32Exception(error, $"{operation?.Split('(')[0]} failed (Win32 {error}): {new Win32Exception(error).Message}");
    }

    [StructLayout(LayoutKind.Sequential)] private struct LUID { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] private struct TOKEN_PRIVILEGES { public uint Count; public LUID Luid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct STARTUPINFO
    {
        public int cb; public string? lpReserved, lpDesktop, lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public ushort wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public uint dwProcessId, dwThreadId; }
    [StructLayout(LayoutKind.Sequential)] private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit;
        public UIntPtr Affinity; public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [DllImport("kernel32.dll")] internal static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DuplicateTokenEx(IntPtr existing, uint access, IntPtr attributes, int impersonation, int type, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool SetTokenInformation(IntPtr token, int type, ref uint value, int size);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool LookupPrivilegeValue(string? system, string name, out LUID luid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES privileges, uint length, IntPtr previous, IntPtr returned);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessAsUser(IntPtr token, string application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string directory, ref STARTUPINFO startup, out PROCESS_INFORMATION process);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int type, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint timeout);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}

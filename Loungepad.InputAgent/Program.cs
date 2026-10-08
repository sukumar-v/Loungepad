using System.Diagnostics;
using System.Security.Principal;
using System.Windows.Threading;
using Loungepad.Input;

namespace Loungepad.InputAgent;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (!WindowsIdentity.GetCurrent().IsSystem || Process.GetCurrentProcess().SessionId != DesktopApi.WTSGetActiveConsoleSessionId())
            throw new UnauthorizedAccessException("Only the LocalSystem service may start the console input agent");
        if (args.Length == 2 && args[0] == "--desktop" && args[1] is "Default" or "Winlogon")
        {
            string stage = "opening input desktop";
            // The process was bound to its desktop by CreateProcess before CLR/COM startup.
            try
            {
                using var desktop = DesktopApi.Open();
                if (desktop.Name != args[1]) return; // desktop changed while this worker was starting
                stage = "verifying thread desktop";
                DesktopApi.VerifyWorkerDesktop(args[1]);
                stage = "initializing input worker";
                new DesktopWorker(args[1]).Run();
            }
            catch (Exception ex)
            {
                MachineInputSettings.ReportError($"Desktop worker failed while {stage}: {Describe(ex)}");
                Environment.ExitCode = 1;
            }
            return;
        }
        if (args.Length != 0) throw new ArgumentException("Invalid input agent arguments");
        SuperviseDesktops();
    }

    private static void SuperviseDesktops()
    {
        Process? worker = null;
        string? name = null;
        long started = 0, retryAt = 0;
        int failures = 0;
        try
        {
            while (true)
            {
                try
                {
                    using var desktop = DesktopApi.Open();
                    long now = Environment.TickCount64;
                    if (name != desktop.Name)
                    {
                        // New process per desktop: WPF render resources and HWNDs cannot be moved
                        // with SetThreadDesktop after creation, even on a fresh dispatcher thread.
                        if (worker?.HasExited == false) { worker.Kill(entireProcessTree: true); worker.WaitForExit(2000); }
                        worker?.Dispose(); worker = null; name = desktop.Name;
                        failures = 0; retryAt = 0;
                    }
                    if (worker?.HasExited == true)
                    {
                        failures = now - started >= 30000 ? 1 : Math.Min(failures + 1, 5);
                        retryAt = now + Math.Min(30000, 1000 << failures);
                        worker.Dispose(); worker = null;
                    }
                    if (worker is null && now >= retryAt && name is "Default" or "Winlogon")
                    {
                        retryAt = now + 30000;
                        worker = DesktopApi.StartWorker(name);
                        started = now;
                    }
                }
                catch (Exception ex)
                {
                    MachineInputSettings.ReportError($"Desktop supervisor: {Describe(ex)}");
                }
                Thread.Sleep(100);
            }
        }
        finally { if (worker?.HasExited == false) worker.Kill(entireProcessTree: true); worker?.Dispose(); }
    }

    internal static string Describe(Exception ex) => ex is System.ComponentModel.Win32Exception native
        ? $"{native.Message} (Win32 {native.NativeErrorCode})"
        : $"{ex.GetType().Name} (0x{ex.HResult:X8})";
}



using System.Security.Principal;
using System.ServiceProcess;
using Microsoft.Win32;
using Loungepad.Input;

namespace Loungepad.Service;

internal static class Program
{
    internal const string RegistryPath = @"SOFTWARE\Loungepad\Input";
    internal static bool Enabled
    {
        get { using var key = Registry.LocalMachine.OpenSubKey(RegistryPath); return key?.GetValue("Enabled") is int value && value == 1; }
    }

    public static void Main(string[] args)
    {
        if (args is ["--configure", "0" or "1", "0" or "1", _])
        {
            if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
                throw new UnauthorizedAccessException("Changing machine input support requires elevation");
            MachineInputSettings.Save(MachineInputSettings.Parse(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(args[3]))));
            var features = new InputFeatures(args[1] == "1", args[2] == "1");
            features.Save();
            MachineInputSettings.ReportError(null);
            if (features.Enabled)
            {
                using var service = new ServiceController(Protocol.ServiceName);
                if (service.Status == ServiceControllerStatus.Stopped) service.Start();
            }
            return;
        }
        if (args.Length is 1 or 2 && args[0] is "--enable" or "--disable")
        {
            if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
                throw new UnauthorizedAccessException("Changing machine input support requires elevation");
            using var key = Registry.LocalMachine.CreateSubKey(RegistryPath);
            if (args.Length == 2)
                MachineInputSettings.Save(MachineInputSettings.Parse(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(args[1]))));
            new InputFeatures(args[0] == "--enable", args[0] == "--enable").Save();
            MachineInputSettings.ReportError(null);
            if (args[0] == "--enable")
            {
                using var service = new ServiceController(Protocol.ServiceName);
                if (service.Status == ServiceControllerStatus.Stopped) service.Start();
            }
            return;
        }
        if (args.Length != 0 || !WindowsIdentity.GetCurrent().IsSystem)
            throw new UnauthorizedAccessException("Run Loungepad.Service through the Service Control Manager as LocalSystem");
        ServiceBase.Run(new InputService());
    }
}

internal sealed class InputService : ServiceBase
{
    private readonly CancellationTokenSource _stop = new();
    private Task? _supervisor, _pipe;
    private ServiceStatus _status = new(false, null, "disabled");

    public InputService() { ServiceName = Protocol.ServiceName; CanStop = true; AutoLog = true; }

    protected override void OnStart(string[] args)
    {
        _supervisor = Task.Run(Supervise);
        _pipe = Task.Run(ServeStatus);
    }

    protected override void OnStop()
    {
        _stop.Cancel();
        try { Task.WaitAll(new[] { _supervisor!, _pipe! }, TimeSpan.FromSeconds(10)); }
        catch (AggregateException ex) when (ex.InnerExceptions.All(e => e is OperationCanceledException)) { }
    }

    private async Task Supervise()
    {
        SessionProcess? child = null;
        uint childSession = uint.MaxValue;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    bool enabled = Program.Enabled;
                    uint session = SessionProcess.WTSGetActiveConsoleSessionId();
                    if (!enabled || session == uint.MaxValue || session != childSession || child?.HasExited == true)
                    {
                        child?.Dispose(); child = null; childSession = uint.MaxValue;
                    }
                    if (enabled && session != uint.MaxValue && child is null)
                    {
                        MachineInputSettings.ClearAgent();
                        child = SessionProcess.Start(session);
                        childSession = session;
                        MachineInputSettings.ReportError(null);
                    }
                    var agent = MachineInputSettings.Agent();
                    bool responding = child is not null && agent is not null && agent.Session == session
                        && Environment.TickCount64 - agent.Tick is >= 0 and < 3000;
                    Volatile.Write(ref _status, new(enabled, child is null ? null : checked((int)session),
                        !enabled ? "disabled" : child is null ? "waiting for console" : !responding ? "Waiting for input agent or selected desktop"
                        : !agent!.ControllerPresent ? $"No controller detected ({agent.Desktop})"
                        : !agent.Ready ? "Release controller controls to start"
                        : $"Active on {agent.Desktop} ({agent.Mode})", enabled ? MachineInputSettings.Error() : null,
                        responding ? agent : null));
                }
                catch (Exception ex)
                {
                    child?.Dispose(); child = null; childSession = uint.MaxValue;
                    MachineInputSettings.ReportError(ex.Message);
                    Volatile.Write(ref _status, new(Program.Enabled, null, "error", ex.Message));
                }
                await Task.Delay(500, _stop.Token);
            }
        }
        catch (OperationCanceledException) { }
        finally { child?.Dispose(); }
    }

    private async Task ServeStatus()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = Protocol.Server(Protocol.StatusPipe);
                await pipe.WaitForConnectionAsync(_stop.Token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(1000);
                // Read-only endpoint. Enabling or disabling is never accepted over IPC.
                await Protocol.Write(pipe, Volatile.Read(ref _status), timeout.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
            catch (Exception) { await Task.Delay(500, _stop.Token).ConfigureAwait(false); }
        }
    }
}

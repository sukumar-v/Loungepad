using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Windows.Threading;
using Loungepad.Input;
using Loungepad.Interop;
using Loungepad.Services;

namespace Loungepad.InputAgent;

internal sealed class DesktopWorker
{
    private readonly string _desktop;
    private readonly CancellationTokenSource _stop = new();
    private readonly HidGamepadReader _hid = new();
    private readonly InputInjector _injector = new();
    private Dispatcher _dispatcher = null!;
    private SecureMapper? _mapper;
    private GamepadNavigationFilter? _navigation;
    private InputProfile _profile = new();
    private long _lastClient, _nextDevices, _nextHealth;
    private bool _remoteActive, _remoteMove, _remoteWheel;
    private AgentReply? _latest;
    private readonly NeutralInputGate _gate = new();
    private int _xboxIndex;
    private readonly NativeMethods.XINPUT_GAMEPAD[] _previous = new NativeMethods.XINPUT_GAMEPAD[4];

    public DesktopWorker(string desktop) => _desktop = desktop;

    public void Run()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        NativeMethods.InputSink = inputs => DesktopApi.IsCurrent(_desktop) ? _injector.SendLocal(inputs) : 0;
        _profile = MachineInputSettings.Load();
        _hid.RefreshDirectDevices();
        _hid.SetQuiet(true);
        _mapper = new SecureMapper(_profile, _injector);
        // On a thread of its own: a low-level hook whose thread stalls past Windows' timeout is
        // removed silently, and this dispatcher thread shows the secure keyboard, a WPF window.
        using var navigation = _navigation = new GamepadNavigationFilter(() => _profile.MouseEnabled);
        // Clear synthetic drags left by a terminated desktop worker before accepting fresh presses.
        _injector.ReleaseMouse();
        // DispatcherTimer uses the coarse Windows message timer. Drive the STA dispatcher
        // from a precise clock instead; await every tick so slow work cannot queue a burst.
        var pump = Task.Factory.StartNew(() =>
        {
            try
            {
                using var cadence = new InputCadence();
                while (!_stop.IsCancellationRequested)
                {
                    cadence.Wait();
                    _dispatcher.InvokeAsync(() =>
                    {
                        try { Tick(); }
                        catch (Exception ex)
                        {
                            MachineInputSettings.ReportError($"Input agent: {Program.Describe(ex)}");
                            _injector.Release();
                        }
                    }, DispatcherPriority.Input, _stop.Token).Task.GetAwaiter().GetResult();
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception ex)
            {
                MachineInputSettings.ReportError($"Input clock: {Program.Describe(ex)}");
                _dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task? server = _desktop == "Default" ? Task.Run(Serve) : null;
        try { Dispatcher.Run(); }
        finally
        {
            _stop.Cancel(); _mapper?.Dispose(); _hid.Dispose();
            if (DesktopApi.IsCurrent(_desktop)) _injector.Release();
            try { server?.Wait(1500); } catch (AggregateException) { }
            try { pump.Wait(1500); } catch (AggregateException) { }
        }
    }

    /// <summary>Milliseconds on the fine clock, for movement. TickCount64 is quantized to the
    /// Windows system clock (often ~15.6 ms): used for dt it alternated zero movement and a
    /// double step even with an 8 ms wake-up timer. It stays for housekeeping and timeouts.</summary>
    private static double Now() => Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency);

    private void Tick()
    {
        if (!DesktopApi.IsCurrent(_desktop)) { _stop.Cancel(); _dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); return; }
        long now = Environment.TickCount64;
        if (now >= _nextDevices) { _nextDevices = now + 1000; _hid.RefreshDirectDevices(); }
        if (_desktop == "Default" && _remoteActive && now - _lastClient <= 250)
        {
            // The launcher owns the buttons and decides the policy; the pointer itself is moved
            // here, from a fresh reading, in this loop's own cadence, with the same formula as on
            // a secure desktop. Nothing about a stick movement crosses the pipe any more. The
            // touch travel is left in the reader for the next reply: the touchpad is still the
            // launcher's.
            if ((_remoteMove || _remoteWheel) && _gate.Armed)
                _mapper!.MovePointer(Capture(drainTouch: false), Now(), _remoteMove, _remoteWheel);
            ReportHealth(now);
            return;
        }
        if (_remoteActive) RelinquishRemote();
        var snapshot = Capture();
        if (!snapshot.XInputPresent && !snapshot.Hid.Present) _mapper!.ResetInput();
        if (_gate.Accept(snapshot)) _mapper!.Update(snapshot, Now());
        ReportHealth(now);
    }

    private void ReportHealth(long now)
    {
        if (now < _nextHealth) return;
        _nextHealth = now + 1000;
        MachineInputSettings.ReportError(null);
        MachineInputSettings.ReportAgent(new(now, Process.GetCurrentProcess().SessionId, Environment.ProcessId,
            _desktop, _remoteActive ? "launcher connected" : "background input",
            _latest?.XInputPresent == true || _latest?.Hid.Present == true, _gate.Armed,
            _navigation?.BlockedCount ?? 0));
    }

    private void RelinquishRemote()
    {
        _remoteActive = false; _remoteMove = _remoteWheel = false; _mapper!.Relinquish(); _gate.Reset();
    }

    private AgentReply Capture(bool drainTouch = true)
    {
        bool present = false;
        NativeMethods.XINPUT_STATE xbox = default;
        for (int i = 0; i < 4; i++)
        {
            try
            {
                if (NativeMethods.XInputGetStateAny(i, out var state) != 0) continue;
                if (!state.Gamepad.Equals(_previous[i])) _xboxIndex = i;
                _previous[i] = state.Gamepad;
                if (!present || i == _xboxIndex) { xbox = state; present = true; }
            }
            catch (DllNotFoundException) { break; }
        }
        return _latest = new(Protocol.Version, _desktop == "Default", present, xbox, _hid.Snapshot(drainTouch), PointerOwner: true);
    }

    private async Task Serve()
    {
        var user = DesktopApi.ConsoleUser();
        if (user is null) return; // no user IPC before login, including on Winlogon
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = Protocol.Server(Protocol.AgentPipe(Process.GetCurrentProcess().SessionId), user);
                await pipe.WaitForConnectionAsync(_stop.Token);
                if (!DesktopApi.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid)
                    || !DesktopApi.ProcessIdToSessionId(pid, out uint session)
                    || session != Process.GetCurrentProcess().SessionId)
                    throw new UnauthorizedAccessException("Input client is not in this console session");
                bool authorized = false;
                pipe.RunAsClient(() => { using var identity = WindowsIdentity.GetCurrent(); authorized = identity.User == user; });
                if (!authorized) throw new UnauthorizedAccessException("Input client is not the console user");
                while (pipe.IsConnected && !_stop.IsCancellationRequested)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    timeout.CancelAfter(1000);
                    var request = await Protocol.Read<AgentRequest>(pipe, timeout.Token);
                    if (request.Version != Protocol.Version || request.Events is null || request.Events.Length > 128)
                        throw new IOException("Unsupported input protocol");
                    request.Profile?.Validate();
                    var reply = await _dispatcher.InvokeAsync(() => Handle(request), DispatcherPriority.Input, timeout.Token);
                    await Protocol.Write(pipe, reply, timeout.Token);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
            catch (Exception) { /* a malformed/disconnected client cannot stop physical secure input */ }
            if (!_stop.IsCancellationRequested)
            {
                await _dispatcher.InvokeAsync(RelinquishRemote);
                await Task.Delay(100, _stop.Token);
            }
        }
    }

    private AgentReply Handle(AgentRequest request)
    {
        if (!DesktopApi.IsCurrent("Default")) throw new IOException("Desktop changed");
        if (!_remoteActive) { _mapper!.Relinquish(); _gate.Reset(); _remoteActive = true; }
        _lastClient = Environment.TickCount64;
        _remoteMove = request.MovePointer; _remoteWheel = request.ScrollWheel;
        if (request.Profile is { } profile && profile != _profile)
        {
            MachineInputSettings.Save(profile); _profile = profile;
            _mapper!.Dispose(); _mapper = new SecureMapper(profile, _injector);
        }
        // Direct HID reads already honor quiet mode and avoid keeping displays awake.
        var snapshot = Capture();
        if (!_gate.Accept(snapshot))
        {
            _injector.Release();
            return new(Protocol.Version, true, false, default, default, PointerOwner: true);
        }
        var events = request.Events.Select(e => e.ToNative()).ToArray();
        if (events.Length > 0) _injector.Send(events);
        return snapshot;
    }
}

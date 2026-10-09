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
    private bool _remoteActive, _remoteMove, _remoteWheel, _desktopCurrent;
    private AgentReply? _latest;
    private readonly NeutralInputGate _gate = new();
    private int _xboxIndex;
    private readonly NativeMethods.XINPUT_GAMEPAD[] _previous = new NativeMethods.XINPUT_GAMEPAD[4];
    // Which XInput slots answered last time. Only those are read on the tick: asking an empty slot
    // goes through GameInput on this Windows and can take tens of milliseconds in a SYSTEM process
    // (the Default worker's tick ran every 40-70 ms with four of them, Oct 8 2026, while the
    // Winlogon worker, where XInput fails at once, was smooth). The empty ones are probed from a
    // thread of their own every 500 ms, and how long that took is in the health record.
    private readonly bool[] _xboxConnected = new bool[4];
    private double _xboxProbeMax;
    // Where a tick's time went over the current health period. Nothing short of these numbers
    // could say which call was slow in a SYSTEM process.
    private long _tickStarted; private int _ticks;
    private double _tickSum, _tickMax, _captureMax, _mapMax, _sendMax; private int _sendShort;

    public DesktopWorker(string desktop) => _desktop = desktop;

    private static double Ms(long from) => (Stopwatch.GetTimestamp() - from) * 1000.0 / Stopwatch.Frequency;

    public void Run()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        // The desktop is checked once per tick (Tick's first line) and the result is what every
        // injection consults. It is checked here first as well: the release of stuck buttons
        // below is an injection, and a sink that answered 0 before the first tick made the worker
        // throw at start-up, exit, and be restarted by the supervisor for ever (Oct 8 2026, the
        // build of 20:40: no worker ran at all, and the launcher moved the pointer by itself).
        _desktopCurrent = DesktopApi.IsCurrent(_desktop);
        NativeMethods.InputSink = inputs =>
        {
            if (!_desktopCurrent) return 0;
            long started = Stopwatch.GetTimestamp();
            uint sent = _injector.SendLocal(inputs);
            _sendMax = Math.Max(_sendMax, Ms(started));
            if (sent != inputs.Length) _sendShort++;
            return sent;
        };
        var xinputProbe = Task.Factory.StartNew(ProbeXInputSlots, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
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
            try { xinputProbe.Wait(1500); } catch (AggregateException) { }
        }
    }

    /// <summary>Asks the XInput slots that did not answer last time whether a pad has arrived,
    /// twice a second, off the tick. A connected slot is read on the tick itself.</summary>
    private void ProbeXInputSlots()
    {
        while (!_stop.IsCancellationRequested)
        {
            for (int i = 0; i < 4 && !_stop.IsCancellationRequested; i++)
            {
                if (Volatile.Read(ref _xboxConnected[i])) continue;
                long started = Stopwatch.GetTimestamp();
                bool connected;
                try { connected = NativeMethods.XInputGetStateAny(i, out _) == 0; }
                catch (DllNotFoundException) { return; }
                double took = Ms(started);
                if (took > _xboxProbeMax) _xboxProbeMax = took;
                if (connected) Volatile.Write(ref _xboxConnected[i], true);
            }
            try { Task.Delay(500, _stop.Token).GetAwaiter().GetResult(); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Milliseconds on the fine clock, for movement. TickCount64 is quantized to the
    /// Windows system clock (often ~15.6 ms): used for dt it alternated zero movement and a
    /// double step even with an 8 ms wake-up timer. It stays for housekeeping and timeouts.</summary>
    private static double Now() => Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency);

    private void Tick()
    {
        long started = Stopwatch.GetTimestamp();
        if (_tickStarted != 0) { double gap = (started - _tickStarted) * 1000.0 / Stopwatch.Frequency; _tickSum += gap; _tickMax = Math.Max(_tickMax, gap); _ticks++; }
        _tickStarted = started;
        _desktopCurrent = DesktopApi.IsCurrent(_desktop);
        if (!_desktopCurrent) { _stop.Cancel(); _dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); return; }
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
            {
                long captured = Stopwatch.GetTimestamp();
                var reading = Capture(drainTouch: false);
                _captureMax = Math.Max(_captureMax, Ms(captured));
                long mapped = Stopwatch.GetTimestamp();
                _mapper!.MovePointer(reading, Now(), _remoteMove, _remoteWheel);
                _mapMax = Math.Max(_mapMax, Ms(mapped));
            }
            ReportHealth(now);
            return;
        }
        if (_remoteActive) RelinquishRemote();
        long capture = Stopwatch.GetTimestamp();
        var snapshot = Capture();
        _captureMax = Math.Max(_captureMax, Ms(capture));
        if (!snapshot.XInputPresent && !snapshot.Hid.Present) _mapper!.ResetInput();
        long map = Stopwatch.GetTimestamp();
        if (_gate.Accept(snapshot)) _mapper!.Update(snapshot, Now());
        _mapMax = Math.Max(_mapMax, Ms(map));
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
            _navigation?.BlockedCount ?? 0,
            TickMeanMs: _ticks == 0 ? 0 : Math.Round(_tickSum / _ticks, 2), TickMaxMs: Math.Round(_tickMax, 2),
            CaptureMaxMs: Math.Round(_captureMax, 2), MapMaxMs: Math.Round(_mapMax, 2), SendMaxMs: Math.Round(_sendMax, 2),
            SendShort: _sendShort, Ticks: _ticks, XInputProbeMaxMs: Math.Round(_xboxProbeMax, 2),
            XInputSlots: _xboxConnected.Count(c => c)));
        _ticks = 0; _tickSum = _tickMax = _captureMax = _mapMax = _sendMax = 0; _sendShort = 0; _xboxProbeMax = 0;
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
            if (!Volatile.Read(ref _xboxConnected[i])) continue;   // empty slots are probed off the tick
            try
            {
                if (NativeMethods.XInputGetStateAny(i, out var state) != 0) { Volatile.Write(ref _xboxConnected[i], false); continue; }
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

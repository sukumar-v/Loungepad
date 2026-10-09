using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Loungepad.Input;
using Loungepad.Interop;
using Loungepad.Models;
using Microsoft.Win32;

namespace Loungepad.Services;

internal sealed class ServiceInputClient : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly Queue<NativeMethods.INPUT[]> _pending = new();
    private readonly AutoResetEvent _inputWake = new(false);
    private AgentReply? _snapshot;
    private InputProfile _profile = new();
    private long _lastReply;
    private bool _connected;
    private Task? _inputTask, _statusTask;
    private bool _installed, _enabled, _olderAgent;
    private InputFeatures _features = new(false, false);
    private bool _movePointer, _scrollWheel;
    private long _policyAt;
    private string? _operation;
    private int? _progress;
    private InputServiceUiStatus? _shown;
    private Version? _installedVersion;
    public ServiceStatus Status { get; private set; } = new(false, null, "service unavailable");
    public InputServiceUiStatus UiStatus
    {
        get
        {
            lock (_gate)
            {
                string state = Status.State switch { "agent running" => "Enabled", "disabled" => "Disabled",
                    "waiting for console" => "Waiting for a console session", "error" => "Error", _ => Status.State };
                if (_olderAgent && _enabled && _connected) state += " (the installed service is older than this Loungepad: update it below)";
                var update = UpdateTarget(_installed, _installedVersion);
                return new(_enabled, _installed, _operation ?? state, Status.Error, _operation is not null, _progress,
                    _features.Uac, _features.SignIn, _installedVersion is { } v ? UpdateService.Format(v) : null,
                    update is { } u ? UpdateService.Format(u) : null);
            }
        }
    }

    /// <summary>
    /// The version to update the installed service to, or null when there is nothing to do. The
    /// launcher installs the service published with its own release, so an update is due whenever
    /// the installed one was built as an older version than this launcher. Never offered by a
    /// build that cannot install at all (an unsigned development build), nor for a service newer
    /// than the launcher.
    /// </summary>
    private static Version? UpdateTarget(bool installed, Version? installedVersion) =>
        UpdateTarget(installed, installedVersion, InputServiceSetup.Bundled, InputServiceSetup.CanInstall);

    /// <summary>The rule itself, apart from where its inputs come from, for the harness.</summary>
    internal static Version? UpdateTarget(bool installed, Version? installedVersion, Version bundled, bool canInstall) =>
        installed && installedVersion is { } v && v < bundled && canInstall ? bundled : null;

    /// <summary>The update due right now, read fresh (for the start-up question, before the
    /// status loop has run): the installed version, the version it would become, and whether a
    /// switch is on. Null when nothing is due.</summary>
    public (Version Installed, Version Target, bool InUse)? PendingUpdate()
    {
        try
        {
            if (!InputServiceSetup.IsInstalled()) return null;
            var installed = InputServiceSetup.InstalledVersion();
            if (installed is null || UpdateTarget(true, installed) is not { } target) return null;
            return (installed, target, InputFeatures.Load().Enabled);
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// The launcher's policy for this tick: whether the agent should move the pointer with the
    /// left stick and scroll with the right one. Refreshed every tick while GamepadService is in
    /// its mouse branch; a stamp older than 50 ms reads as off, so every path that leaves that
    /// branch -- a menu, a focused game, rest mode, the pad going quiet -- stops the agent
    /// without having to name it.
    /// </summary>
    public void SetPointerPolicy(bool movePointer, bool scrollWheel)
    {
        lock (_gate) { _movePointer = movePointer; _scrollWheel = scrollWheel; _policyAt = Stopwatch.GetTimestamp(); }
    }

    private (bool Move, bool Wheel) PointerPolicy()
    {
        lock (_gate) return Stopwatch.GetElapsedTime(_policyAt).TotalMilliseconds < 50 ? (_movePointer, _scrollWheel) : (false, false);
    }
    public bool Connected => Volatile.Read(ref _connected) && Environment.TickCount64 - Interlocked.Read(ref _lastReply) < 250;
    public event Action<bool>? ConnectionChanged;
    public event Action? StatusChanged;

    public void Start(AppSettings settings)
    {
        UpdateProfile(settings);
        NativeMethods.InputSink = Send;
        _inputTask = Task.Run(InputLoop); _statusTask = Task.Run(StatusLoop);
    }

    public AgentReply? Snapshot()
    {
        lock (_gate)
        {
            if (!Connected || _snapshot is null) return null;
            var result = _snapshot;
            _snapshot = result with { Hid = result.Hid with { TouchDx = 0, TouchDy = 0, TouchSpread = 0 } };
            return result;
        }
    }

    private uint Send(NativeMethods.INPUT[] inputs)
    {
        lock (_gate)
        {
            if (!Connected) return NativeMethods.SendInputLocal(inputs);
            if (inputs.Length > 128 || _pending.Count >= 64) return 0;
            _pending.Enqueue(inputs);
            _inputWake.Set();
            return (uint)inputs.Length;
        }
    }

    private static NamedPipeClientStream Client(string name) => new(".", name,
        PipeDirection.InOut, PipeOptions.Asynchronous,
        TokenImpersonationLevel.Identification, HandleInheritability.None);

    private async Task InputLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = Client(Protocol.AgentPipe(Process.GetCurrentProcess().SessionId));
                await pipe.ConnectAsync(500, _stop.Token);
                Protocol.VerifyServer(pipe);
                using var cadence = new InputCadence();
                InputProfile? sentProfile = null;
                while (!_stop.IsCancellationRequested)
                {
                    var events = new List<InputEvent>();
                    lock (_gate)
                    {
                        while (_pending.Count > 0 && events.Count + _pending.Peek().Length <= 128)
                            events.AddRange(_pending.Dequeue().Select(InputEvent.From));
                    }
                    var profile = Volatile.Read(ref _profile);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    timeout.CancelAfter(500);
                    var (movePointer, scrollWheel) = PointerPolicy();
                    await Protocol.Write(pipe, new AgentRequest(Protocol.Version, events.ToArray(), Profile: sentProfile == profile ? null : profile,
                        MovePointer: movePointer, ScrollWheel: scrollWheel), timeout.Token);
                    var reply = await Protocol.Read<AgentReply>(pipe, timeout.Token);
                    if (reply.Version != Protocol.Version || !reply.DefaultDesktop) throw new IOException("Unsupported input agent");
                    sentProfile = profile;
                    lock (_gate)
                    {
                        var h = reply.Hid;
                        if (_snapshot?.Hid is { Present: true } previous && previous.InstanceId == h.InstanceId)
                            h = h with { TouchDx = h.TouchDx + previous.TouchDx, TouchDy = h.TouchDy + previous.TouchDy, TouchSpread = h.TouchSpread + previous.TouchSpread };
                        _snapshot = reply with { Hid = h };
                        _olderAgent = !reply.PointerOwner;
                        Interlocked.Exchange(ref _lastReply, Environment.TickCount64);
                    }
                    SetConnected(true);
                    cadence.Wait(_inputWake);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception) { /* portable/disabled installs retain their existing local input */ }
            SetConnected(false);
            try { await Task.Delay(500, _stop.Token); } catch (OperationCanceledException) { break; }
        }
        SetConnected(false);
    }

    private void SetConnected(bool connected)
    {
        if (_connected == connected) return;
        lock (_gate) { _connected = connected; if (!connected) { _snapshot = null; _pending.Clear(); } }
        ConnectionChanged?.Invoke(connected);
    }

    private async Task StatusLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            try { await RefreshStatus(); }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            try { await Task.Delay(2000, _stop.Token); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task RefreshStatus()
    {
        bool installed = false, enabled = false;
        var features = new InputFeatures(false, false);
        ServiceStatus status;
        try
        {
            installed = InputServiceSetup.IsInstalled();
            if (installed) features = InputFeatures.Load();
            enabled = features.Enabled;
            if (!installed) status = new(false, null, "Not installed");
            else
            {
                using var pipe = Client(Protocol.StatusPipe);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(1000);
                await pipe.ConnectAsync(timeout.Token); Protocol.VerifyServer(pipe);
                status = await Protocol.Read<ServiceStatus>(pipe, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { throw; }
        catch (Exception ex) { status = new(enabled, null, installed ? "Service stopped or unavailable" : "Unable to check installation", ex.Message); }
        // Only a change Settings can show is a change. The raw status carries the worker's health
        // record, whose tick counter and timings are new on every heartbeat, so comparing that
        // pushed the status to the page every 2 s -- and each push re-rendered Settings, which
        // scrolled a list the user had scrolled away back to the highlight.
        // Compared with what was last pushed rather than with the moment before, so a change made
        // outside this poll (the connection, an older agent's reply) still reaches the page within 2 s.
        var installedVersion = installed ? InputServiceSetup.InstalledVersion() : null;
        lock (_gate) { Status = status; _installed = installed; _enabled = enabled; _features = features; _installedVersion = installedVersion; }
        var shown = UiStatus;
        if (shown == _shown) return;
        _shown = shown;
        StatusChanged?.Invoke();
    }

    private void ReportSetup(string? state, int? progress)
    {
        lock (_gate) { _operation = state; _progress = progress; }
        StatusChanged?.Invoke();
    }

    public async Task SetEnabled(string feature, bool enabled, AppSettings settings, bool installConfirmed)
    {
        if (feature is not ("uac" or "signIn")) throw new ArgumentException("Unknown input feature");
        bool installed = InputServiceSetup.IsInstalled();
        if (!installed && !enabled) { await RefreshStatus(); return; }
        if (!installed && !installConfirmed) throw new InputServiceInstallRequiredException();
        var current = installed ? InputFeatures.Load() : new(false, false);
        var features = feature == "uac" ? current with { Uac = enabled } : current with { SignIn = enabled };
        UpdateProfile(settings);
        string profile = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(_profile, Protocol.Json)));
        // An installed service older than this launcher is updated in the same step: one approval
        // prompt either way, and the switch lands on the service this launcher was built against.
        // A release published without the package falls back to switching the installed one.
        bool update = installed && UpdateTarget(true, InputServiceSetup.InstalledVersion()) is not null;
        ReportSetup(installed && !update ? "Waiting for administrator approval" : "Preparing input service", null);
        try
        {
            if (!installed) await InputServiceSetup.Install(profile, features, ReportSetup, _stop.Token);
            else if (update)
            {
                try { await InputServiceSetup.Install(profile, features, ReportSetup, _stop.Token); }
                catch (InputServicePackageMissingException) { ReportSetup("Waiting for administrator approval", null); await Configure(features, profile); }
            }
            else await Configure(features, profile);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { throw new OperationCanceledException("Administrator approval was cancelled", ex); }
        finally { await RefreshStatus(); ReportSetup(null, null); }
    }

    private static async Task Configure(InputFeatures features, string profile)
    {
        string exe = InputServiceSetup.ServiceExe;
        if (!File.Exists(exe)) throw new IOException("Input service files are missing. Uninstall the service and enable it again to reinstall.");
        using var process = Process.Start(new ProcessStartInfo(exe, $"--configure {(features.Uac ? 1 : 0)} {(features.SignIn ? 1 : 0)} " + profile)
        { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden }) ?? throw new IOException("Could not open input service configuration");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new IOException("Input service configuration failed. If the service is from an older build, update it from Settings > Advanced.");
        if (InputFeatures.Load() != features) throw new IOException("Input service did not save the requested feature switches");
    }

    /// <summary>
    /// Replace the installed service with the one published with this launcher's release: the same
    /// download, signature checks and elevated installer as a first install, with the switches and
    /// the profile as they are now. The installer stops the service, swaps its folder and starts it
    /// again. Returns the version now installed.
    /// </summary>
    public async Task<string> Update(AppSettings settings)
    {
        if (!InputServiceSetup.IsInstalled()) throw new IOException("The input service is not installed");
        var features = InputFeatures.Load();
        UpdateProfile(settings);
        string profile = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(_profile, Protocol.Json)));
        ReportSetup("Preparing input service update", null);
        try
        {
            await InputServiceSetup.Install(profile, features, ReportSetup, _stop.Token);
            var now = InputServiceSetup.InstalledVersion();
            Log.Info($"Input service updated to {(now is null ? "an unreadable version" : UpdateService.Format(now))}");
            return now is null ? UpdateService.Format(InputServiceSetup.Bundled) : UpdateService.Format(now);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { throw new OperationCanceledException("Administrator approval was cancelled", ex); }
        finally { await RefreshStatus(); ReportSetup(null, null); }
    }

    public async Task Uninstall()
    {
        if (!InputServiceSetup.IsInstalled()) { await RefreshStatus(); return; }
        ReportSetup("Waiting for administrator approval / uninstalling", null);
        try { await InputServiceSetup.Uninstall(); }
        finally { await RefreshStatus(); ReportSetup(null, null); }
    }

    public void UpdateProfile(AppSettings s) => Volatile.Write(ref _profile, new InputProfile
    {
        MouseEnabled = s.GamepadMouseEnabled, Deadzone = s.Deadzone, Sensitivity = s.Sensitivity, AccelExponent = s.AccelExponent,
        BoostButton = s.BoostButton, BoostMultiplier = s.BoostMultiplier, LeftClick = s.LeftClickButton, RightClick = s.RightClickButton,
        TouchpadMouse = s.TouchpadMouse, TouchpadSensitivity = s.TouchpadSensitivity, TouchpadTapToClick = s.TouchpadTapToClick,
        TouchpadTapDrag = s.TouchpadTapDrag, TouchpadNaturalScroll = s.TouchpadNaturalScroll, TouchpadScrollSpeed = s.TouchpadScrollSpeed,
        KeyboardApp = s.KeyboardApp, KeyboardToggle = s.KeyboardToggleButton, KeyboardToggleMode = s.KeyboardToggleMode,
        KeyboardToggleHoldMs = s.KeyboardToggleHoldMs, KeyboardScale = s.KeyboardScale, FunctionKeys = s.KeyboardFunctionKeys,
        NavKeys = s.KeyboardNavKeys, Numpad = s.KeyboardNumpad, Modifiers = s.KeyboardModifiers,
        RepeatDelayMs = s.KeyRepeatDelayMs, RepeatIntervalMs = s.KeyRepeatIntervalMs, Display = s.TvDeviceName,
    }.Validate());

    public void Dispose()
    {
        _stop.Cancel(); _inputWake.Set(); NativeMethods.InputSink = null;
        try { Task.WaitAll(new[] { _inputTask!, _statusTask! }, 1500); } catch (AggregateException) { }
    }
}

internal sealed record InputServiceUiStatus(bool Enabled, bool Installed, string State, string? Error, bool Busy, int? Progress,
    bool UacEnabled, bool SignInEnabled, string? InstalledVersion = null, string? UpdateTo = null);
internal sealed class InputServiceInstallRequiredException : Exception { }

using Loungepad.Interop;
using Loungepad.Models;

namespace Loungepad.Services;

/// <summary>
/// Rest mode: what a console does when you put the pad down.
///
/// After a stretch with no input from the pad, the keyboard or the mouse, the TV goes dark, the
/// game is frozen where it stands, and the pad waits for one press to bring both back. Left
/// resting long enough, the PC itself is put to sleep, so a machine forgotten overnight draws a
/// few watts rather than a few hundred.
///
/// Three phases. Awake is ordinary. Resting is the screen off and the game frozen with the
/// launcher still running: it costs a desktop at idle, and it comes back the instant a button is
/// pressed on ANY controller, because it is this process reading the pad rather than the hardware
/// waking the machine. Asleep is Windows' own sleep (S3, or Modern Standby where the board has
/// it), reached from Resting after the configured time -- or straight away, or never -- and left
/// when Windows says the machine is back. What can wake THAT is the hardware's business (see
/// WakeInfo), which is why the two are separate steps rather than one: the first works
/// everywhere, the second saves the electricity.
///
/// Windows sleeping on its own -- its timer, the power button, the Start menu -- is treated as the
/// same rest minus the dimming, so the game is frozen for that too and thawed only when somebody
/// is back. A wake that lands on the lock screen waits for the sign-in, or the game would play to
/// a screen nobody can get past with a pad.
///
/// Every outside effect goes through <see cref="Ports"/>, so the harness drives it with a fake
/// clock and nothing on the real machine is dimmed, frozen or slept. Everything here runs on one
/// thread (the UI thread in the app: the timer, the window messages and the pad's wake all arrive
/// there).
/// </summary>
public sealed class RestService
{
    public enum Phase { Awake, Resting, Asleep }

    public sealed class Ports
    {
        /// <summary>A clock in milliseconds that keeps counting through sleep; Environment.TickCount64 in the app.</summary>
        public Func<long> Now = () => Environment.TickCount64;
        /// <summary>Milliseconds since a key or a mouse button went down anywhere in the session. The
        /// only keyboard-and-mouse event that wakes a rest: movement never does.</summary>
        public Func<long> UserPressAgeMs = () => long.MaxValue / 2;
        /// <summary>Milliseconds since any deliberate keyboard or mouse use -- a press, the wheel, or
        /// movement that adds up -- for the idle timer.</summary>
        public Func<long> UserActivityAgeMs = () => long.MaxValue / 2;
        /// <summary>Milliseconds since a gamepad was last touched: a button, a stick past the noise, the touchpad.</summary>
        public Func<long> PadInputAgeMs = () => long.MaxValue / 2;
        public Func<bool> GameRunning = () => false;
        /// <summary>Something other than the launcher is keeping the display awake -- a video
        /// playing in a browser. Games always hold it, so this is only asked with no game running.</summary>
        public Func<bool> DisplayHeldAwake = () => false;
        public Func<bool> SleepAllowed = () => true;
        public Action DisplaysOff = () => { };
        public Action DisplaysOn = () => { };
        /// <summary>Freeze the running game; how many processes froze (0: nothing running, or refused).</summary>
        public Func<int> PauseGame = () => 0;
        public Action ResumeGame = () => { };
        /// <summary>Ask Windows to sleep. Completes with true once the machine is back, or soon with false when refused.</summary>
        public Func<Task<bool>> SleepPc = () => Task.FromResult(false);
        /// <summary>The pad reads as inert (only a press, delivered through Wake, does anything) or normal.</summary>
        public Action<bool> PadInert = _ => { };
        /// <summary>Before resting: menus down, and the launcher on screen when no game is running, so the wake lands somewhere.</summary>
        public Action Prepare = () => { };
        /// <summary>After waking: the pad back in charge of the launcher's UI.</summary>
        public Action AfterWake = () => { };
        public Action<string> Toast = _ => { };
        public Action<string> Log = m => Services.Log.Info(m);
        /// <summary>What Windows says woke the machine, asked after a resume (powercfg /lastwake, so off the UI thread); null when it has no record.</summary>
        public Func<Task<string?>> WakeSource = () => Task.FromResult<string?>(null);
        /// <summary>The pause between preparing and dimming, so a menu's teardown is not caught on screen.</summary>
        public Func<int, Task> Delay = ms => Task.Delay(ms);
    }

    private readonly Func<AppSettings> _settings;
    private readonly Ports _p;

    public Phase Current { get; private set; } = Phase.Awake;
    /// <summary>When the current rest began, on the ports' clock. Meaningless while awake.</summary>
    public long RestingSince { get; private set; }
    /// <summary>Whether this service froze the game, and so owns thawing it. A game the user paused
    /// by hand before the rest is left exactly as they left it.</summary>
    public bool PausedGame { get; private set; }
    public bool SessionLocked { get; private set; }

    /// <summary>The phase changed. The bridge tells the page, which stops its trailers.</summary>
    public event Action? Changed;

    /// <summary>The idle warning goes this long before the rest, once, and only with the launcher on screen.</summary>
    public const int WarnBeforeMs = 60_000;
    /// <summary>"Straight away" still waits this long after the screen has gone dark, so the freeze
    /// and the dimming have settled before the machine goes.</summary>
    public const int SleepSoonMs = 8_000;
    /// <summary>A resume nobody asked for (a maintenance wake looks the same as a keypress until
    /// the keypress arrives) gets this long to become somebody before the machine is put back.</summary>
    public const int ResleepGraceMs = 180_000;
    /// <summary>A refused sleep is tried again after this.</summary>
    public const int SleepRetryMs = 300_000;
    /// <summary>Sleep asked for but no PBT_APMSUSPEND within this long: it did not happen.</summary>
    public const int SleepConfirmMs = 20_000;
    /// <summary>Input this close to the start of a rest is the press that started it, not a wake.</summary>
    public const int RestInputGraceMs = 1_500;
    /// <summary>A display that lit itself while resting is dimmed again after this long...</summary>
    public const int RedimDelayMs = 2_000;
    /// <summary>...and at most this often, so a mouse that will not sit still cannot make the screen flicker.</summary>
    public const int RedimMinGapMs = 10_000;

    private long _inputBaseline;
    private long _noSleepBefore;
    private long _asleepAt;
    /// <summary>When the last wake happened. A wake by the power button, or by a resume Windows
    /// reports, moves neither input stamp, and without this the idle timer would read the hours
    /// asleep as hours idle and rest again on the very next tick.
    ///
    /// The launcher starting counts as one. Every input stamp starts at "never", so with this at
    /// "never" too the first tick read the idle time as forever and rested one to four seconds
    /// after every start (the log, Oct 2026). The timer runs from the start instead.</summary>
    private long _wokeAt;
    private bool _warned;
    private bool _sawSuspend;
    private bool _wakeOnUnlock;
    private bool _entering;
    private long _redimAt;
    private long _lastRedim = long.MinValue / 2;

    public RestService(Func<AppSettings> settings, Ports ports)
    {
        _settings = settings;
        _p = ports;
        _wokeAt = ports.Now();
    }

    /// <summary>Whether any program has told Windows the display must stay on. Browsers and
    /// media players do while a video plays; so does WebView2 for the launcher's own trailers,
    /// which is why the caller only asks when the launcher is not the window in front.</summary>
    public static bool SomethingHoldsDisplay()
    {
        var buf = new byte[4];
        try
        {
            if (NativeMethods.CallNtPowerInformation(NativeMethods.SystemExecutionState, IntPtr.Zero, 0, buf, 4) != 0) return false;
        }
        catch { return false; }
        return (BitConverter.ToUInt32(buf, 0) & NativeMethods.ES_DISPLAY_REQUIRED) != 0;
    }

    /// <summary>Called every half second.</summary>
    public void Tick()
    {
        var s = _settings();
        long now = _p.Now();
        switch (Current)
        {
            case Phase.Awake: TickAwake(s, now); break;
            case Phase.Resting: TickResting(s, now); break;
            case Phase.Asleep:
                // Sleep was asked for and Windows never said it was happening: refused without a
                // word. Back to resting, and try again later.
                if (!_sawSuspend && now - _asleepAt > SleepConfirmMs) SleepRefused(now, "Windows did not go to sleep");
                break;
        }
    }

    private void TickAwake(AppSettings s, long now)
    {
        if (_entering) return;
        int after = s.RestAfterMinutes;
        if (after <= 0) { _warned = false; return; }
        bool game = _p.GameRunning();
        if (game && !s.RestDuringGame) { _warned = false; return; }
        if (!game && _p.DisplayHeldAwake()) { _warned = false; return; }

        long idle = Math.Min(Math.Min(_p.UserActivityAgeMs(), _p.PadInputAgeMs()), now - _wokeAt);
        long limit = after * 60_000L;
        // The warning comes a minute ahead, or halfway for the short timers (a one-minute rest
        // warns at thirty seconds), so there is always a stretch below it where it re-arms.
        long warnBefore = Math.Min(WarnBeforeMs, limit / 2);
        if (idle < limit - warnBefore) { _warned = false; return; }
        if (idle < limit)
        {
            // Only with the launcher on screen: over a game it is parked, and a toast nobody can
            // see is no warning.
            if (!_warned && !game) { _warned = true; _p.Toast("Resting in a minute — press any button to stay on"); }
            return;
        }
        _ = EnterRest($"no input for {after} min", viaWindows: false);
    }

    private void TickResting(AppSettings s, long now)
    {
        if (_entering) return;
        // Somebody pressed a key or a mouse button. Movement is not a wake: a mouse nudged on the
        // desk or a stick drifting through Steam Input would otherwise light a dark room. The pad's
        // buttons come through Wake directly.
        long pressAt = now - _p.UserPressAgeMs();
        if (pressAt > _inputBaseline + RestInputGraceMs) { Wake("a key or a mouse button"); return; }

        // Windows turns the display on by itself for movement it counts as input. Resting means
        // dark, so it is dimmed again a moment later -- once in a while, never in a flicker.
        if (_redimAt != 0 && now >= _redimAt)
        {
            _redimAt = 0;
            _lastRedim = now;
            _p.Log("Rest: the display came on by itself (movement, most likely); dimming it again");
            _p.DisplaysOff();
        }

        int after = s.SleepAfterRestMinutes;
        if (after < 0) return;
        long due = after == 0 ? SleepSoonMs : after * 60_000L;
        if (now - RestingSince < due || now < _noSleepBefore) return;
        TrySleep(now, after == 0 ? "sleep follows rest straight away" : $"resting for {after} min");
    }

    /// <summary>Rest now: the Power Wheel's spoke. Nothing to do if already resting.</summary>
    public void Rest(string reason) => _ = EnterRest(reason, viaWindows: false);

    /// <summary>Sleep the PC now, resting first so the game is frozen and the pad inert when the machine comes back.</summary>
    public async Task Sleep(string reason)
    {
        if (Current == Phase.Awake) await EnterRest(reason, viaWindows: false);
        if (Current == Phase.Resting) TrySleep(_p.Now(), reason);
    }

    private async Task EnterRest(string reason, bool viaWindows)
    {
        if (Current != Phase.Awake || _entering) return;
        _entering = true;
        try
        {
            long now = _p.Now();
            _p.Prepare();
            bool game = _p.GameRunning();
            PausedGame = game && _settings().RestPausesGame && _p.PauseGame() > 0;
            _p.PadInert(true);
            Current = Phase.Resting;
            RestingSince = now;
            _inputBaseline = now;
            _noSleepBefore = 0;
            _warned = false;
            _sawSuspend = false;
            _wakeOnUnlock = false;
            _redimAt = 0;
            _p.Log($"Rest: {reason}"
                   + (game ? PausedGame ? "; the game is frozen" : "; the game keeps running" : "")
                   + (viaWindows ? " (Windows is sleeping)" : ""));
            Changed?.Invoke();
            if (!viaWindows)
            {
                // Let a menu finish closing before the screen goes dark. A press meanwhile has
                // already woken us, and then there is nothing to dim.
                await _p.Delay(250);
                if (Current == Phase.Resting) _p.DisplaysOff();
            }
        }
        finally { _entering = false; }
    }

    /// <summary>
    /// Back from resting: the screen on, the game thawed, the pad in charge. A press while the
    /// session is locked waits for the unlock instead, with the pad kept inert, or the game would
    /// play on behind a lock screen the pad cannot get past.
    /// </summary>
    public void Wake(string source)
    {
        if (Current == Phase.Awake) return;
        if (SessionLocked)
        {
            _wakeOnUnlock = true;
            _p.PadInert(true);
            _p.Log($"Rest: woken by {source}, but the session is locked; waiting for the sign-in");
            return;
        }
        long now = _p.Now();
        long rested = now - RestingSince;
        Current = Phase.Awake;
        _wokeAt = now;
        _wakeOnUnlock = false;
        _redimAt = 0;
        _p.PadInert(false);
        _p.DisplaysOn();
        if (PausedGame) { _p.ResumeGame(); PausedGame = false; }
        _p.AfterWake();
        _p.Log($"Rest: woken by {source} after {Math.Max(0, rested) / 60_000} min");
        Changed?.Invoke();
    }

    /// <summary>
    /// The console display turned on or off (GUID_CONSOLE_DISPLAY_STATE). While resting, a display
    /// that comes on without a wake was lit by Windows for movement it counts as input, and is
    /// scheduled to be dimmed again (see TickResting).
    /// </summary>
    public void OnDisplayState(bool on)
    {
        if (!on) return;
        if (Current != Phase.Resting || _entering) return;
        long now = _p.Now();
        if (now - _lastRedim < RedimMinGapMs) return;
        if (_redimAt == 0) _redimAt = now + RedimDelayMs;
    }

    /// <summary>WM_WTSSESSION_CHANGE: the session locked or unlocked.</summary>
    public void OnSessionLock(bool locked)
    {
        SessionLocked = locked;
        if (!locked && _wakeOnUnlock) Wake("the sign-in");
    }

    /// <summary>WM_POWERBROADCAST, with its wParam.</summary>
    public void OnPowerEvent(int pbt)
    {
        long now = _p.Now();
        switch (pbt)
        {
            case NativeMethods.PBT_APMSUSPEND:
                _sawSuspend = true;
                // Windows sleeping on its own: the same rest, minus the dimming Windows does itself.
                if (Current == Phase.Awake) _ = EnterRest("Windows is going to sleep", viaWindows: true);
                Current = Phase.Asleep;
                _asleepAt = now;
                _p.Log("Rest: the PC is going to sleep");
                Changed?.Invoke();
                break;

            case NativeMethods.PBT_APMRESUMEAUTOMATIC:
                if (Current != Phase.Asleep) break;
                // Back, but not necessarily for anyone: a maintenance wake looks the same as a
                // keypress until the keypress arrives. Rest on, and if nothing is pressed for a
                // while, sleep again.
                Current = Phase.Resting;
                _inputBaseline = now;
                _noSleepBefore = now + ResleepGraceMs;
                _sawSuspend = false;
                _p.Log($"Rest: the PC is back after {Math.Max(0, now - _asleepAt) / 60_000} min asleep; resting until something is pressed");
                // Who woke it, for the log: a PC that comes back by itself seconds after sleeping
                // is being woken by a device (a mouse's sensor, a network card's pattern match),
                // and the name is what the user needs to turn it off in Device Manager.
                _ = LogWakeSourceAsync();
                Changed?.Invoke();
                break;

            case NativeMethods.PBT_APMRESUMESUSPEND:
                if (Current == Phase.Asleep) { Current = Phase.Resting; _sawSuspend = false; }
                Wake("waking the PC");
                break;
        }
    }

    private void TrySleep(long now, string reason)
    {
        if (!_p.SleepAllowed())
        {
            _p.Log("Rest: this PC cannot sleep; resting on");
            _noSleepBefore = long.MaxValue;
            return;
        }
        Current = Phase.Asleep;
        _asleepAt = now;
        _sawSuspend = false;
        _p.Log($"Rest: putting the PC to sleep ({reason})");
        Changed?.Invoke();
        _ = SleepPcAsync(now);
    }

    private async Task SleepPcAsync(long askedAt)
    {
        bool ok;
        try { ok = await _p.SleepPc(); }
        catch (Exception ex) { _p.Log($"Rest: sleep failed: {ex.Message}"); ok = false; }
        // The task completes after the machine is back when the sleep happened, and at once
        // when it was refused; only the second is ours to act on, and only if nothing else has
        // moved the phase on meanwhile.
        if (!ok && Current == Phase.Asleep && _asleepAt == askedAt) SleepRefused(_p.Now(), "Windows refused to sleep");
    }

    private async Task LogWakeSourceAsync()
    {
        try
        {
            var source = await _p.WakeSource();
            _p.Log(source is null ? "Rest: Windows has no record of what woke the PC" : $"Rest: the PC was woken by {source}");
        }
        catch (Exception ex) { _p.Log($"Rest: could not read the wake source: {ex.Message}"); }
    }

    private void SleepRefused(long now, string why)
    {
        Current = Phase.Resting;
        _noSleepBefore = now + SleepRetryMs;
        _p.Log($"Rest: {why}; resting on, and trying again in {SleepRetryMs / 60_000} min");
        Changed?.Invoke();
    }
}

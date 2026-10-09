using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Loungepad.Input;
using Loungepad.InputAgent;
using Loungepad.Interop;
using Loungepad.Services;
using Microsoft.Win32.SafeHandles;

// Read-only measurements of the three things that can make the agent's pointer uneven: what the
// HID reader hands it (a pad on two transports is two pads), how evenly a tick reaches the STA
// dispatcher the way DesktopWorker drives it, and what the real pointer actually did while a
// stick was pushed. Nothing here injects input.
internal static class MotionProbes
{
    /// <summary>Three seconds of snapshots from a reader of our own: which pads, how often the
    /// "last reported" pad changes, each pad's report rate and its stick reading.</summary>
    public static void Hid()
    {
        using var hid = new HidGamepadReader();
        hid.RefreshDirectDevices();
        hid.SetQuiet(true);
        Thread.Sleep(400);
        var samples = new Dictionary<string, int>();
        var seqs = new Dictionary<string, HashSet<long>>();
        var names = new Dictionary<string, string>();
        var sticks = new Dictionary<string, (short LX, short LY)>();
        string? previous = null;
        int flips = 0, present = 0, total = 0;
        var sw = Stopwatch.StartNew();
        using var tick = new MillisecondTimer();
        while (sw.ElapsedMilliseconds < 3000)
        {
            var s = hid.Snapshot();
            total++;
            if (s.Present)
            {
                present++;
                string id = s.InstanceId ?? s.Name;
                samples[id] = samples.GetValueOrDefault(id) + 1;
                (seqs.TryGetValue(id, out var set) ? set : seqs[id] = new()).Add(s.Seq);
                names[id] = $"{s.Name} [{s.Layout}]";
                sticks[id] = (s.Pad.sThumbLX, s.Pad.sThumbLY);
                if (previous is not null && previous != id) flips++;
                previous = id;
            }
            tick.Wait();
        }
        foreach (var (name, path, serial, bluetooth, shadowed) in hid.Describe())
            Console.WriteLine($"instance: {name}  {(bluetooth ? "Bluetooth" : "wired")}  serial='{serial}' (key {HidPad.SerialKey(serial)})  {(shadowed ? "SHADOWED (another instance of the same pad is read)" : "read")}  {path}");
        Console.WriteLine($"HID: {total} snapshots in 3 s, {present} with a pad, {samples.Count} distinct pad instance(s) seen as the reading, last-reporter changed {flips} times");
        foreach (var (id, count) in samples)
            Console.WriteLine($"  {names[id]}  id={id}  share={100.0 * count / present:F0}%  ~{seqs[id].Count / 3.0:F0} reports/s seen as last  stick=({sticks[id].LX},{sticks[id].LY})");
        if (samples.Count > 1)
            Console.WriteLine("  NOTE: more than one HID instance is reporting; a DualSense on USB and Bluetooth at once is two pads to the reader.");
    }

    /// <summary>Records every change of the real pointer's position for the given number of
    /// seconds at about 1 ms resolution, then describes each run of motion: how evenly the moves
    /// arrived and how even the steps were. Move only the controller stick while it runs.</summary>
    public static void PointerTrace(int seconds, bool waitForMotion = false)
    {
        using var tick = new MillisecondTimer();
        var armed = Stopwatch.StartNew();
        while (true)
        {
            if (waitForMotion)
            {
                Console.WriteLine($"Waiting up to 10 minutes for the pointer to move, then tracing it for {seconds} s. A mouse moves the pointer every millisecond or so; the stick's moves come every 8 ms, and only a run shaped like that counts.");
                NativeMethods.GetCursorPos(out var start);
                while (armed.Elapsed.TotalMinutes < 10)
                {
                    if (NativeMethods.GetCursorPos(out var p) && (p.X != start.X || p.Y != start.Y)) break;
                    tick.Wait();
                }
                if (armed.Elapsed.TotalMinutes >= 10) { Console.WriteLine("the pointer never moved like a stick"); return; }
                Console.WriteLine($"motion seen after {armed.Elapsed.TotalSeconds:F0} s");
            }
            var runs = Record(seconds, tick);
            bool stickLike = runs.Any(r => r.Count >= 20 && Median(Intervals(r)) >= 6);
            if (!waitForMotion || stickLike || armed.Elapsed.TotalMinutes >= 10) { Report(runs, waitForMotion); return; }
            Console.WriteLine($"that was a mouse ({runs.Sum(r => r.Count)} changes, none spaced like the stick); waiting again");
        }
    }

    private static List<List<(double T, int X, int Y)>> Record(int seconds, MillisecondTimer tick)
    {
        var moves = new List<(double T, int X, int Y)>();
        int lastX = int.MinValue, lastY = int.MinValue;
        var sw = Stopwatch.StartNew();
        Console.WriteLine($"Tracing the pointer for {seconds} s: push the left stick steadily (a slow push, then a fast one, then diagonal). Do not touch the mouse.");
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            if (NativeMethods.GetCursorPos(out var p) && (p.X != lastX || p.Y != lastY))
            {
                moves.Add((sw.Elapsed.TotalMilliseconds, p.X, p.Y));
                lastX = p.X; lastY = p.Y;
            }
            tick.Wait();
        }
        Console.WriteLine($"{moves.Count} pointer changes recorded");
        // Runs of motion: changes less than 150 ms apart belong to the same push.
        var runs = new List<List<(double T, int X, int Y)>>();
        foreach (var m in moves)
        {
            if (runs.Count == 0 || m.T - runs[^1][^1].T > 150) runs.Add(new());
            runs[^1].Add(m);
        }
        return runs;
    }

    private static List<double> Intervals(List<(double T, int X, int Y)> run)
    {
        var intervals = new List<double>();
        for (int i = 1; i < run.Count; i++) intervals.Add(run[i].T - run[i - 1].T);
        return intervals;
    }

    private static double Median(List<double> values) => values.Count == 0 ? 0 : values.OrderBy(v => v).ElementAt(values.Count / 2);

    private static void Report(List<List<(double T, int X, int Y)>> runs, bool stickOnly)
    {
        foreach (var run in runs.Where(r => r.Count >= 20 && (!stickOnly || Median(Intervals(r)) >= 6)))
        {
            var intervals = Intervals(run);
            var steps = new List<double>();
            for (int i = 1; i < run.Count; i++)
                steps.Add(Math.Sqrt(Math.Pow(run[i].X - run[i - 1].X, 2) + Math.Pow(run[i].Y - run[i - 1].Y, 2)));
            double duration = run[^1].T - run[0].T;
            Console.WriteLine($"run at {run[0].T / 1000:F1} s: {run.Count} moves over {duration:F0} ms, from ({run[0].X},{run[0].Y}) to ({run[^1].X},{run[^1].Y})");
            Console.WriteLine($"  intervals ms: {Describe(intervals)}; over 12 ms: {intervals.Count(i => i > 12)}, over 20 ms: {intervals.Count(i => i > 20)}, over 40 ms: {intervals.Count(i => i > 40)}");
            Console.WriteLine($"  steps px:     {Describe(steps)}");
            Console.WriteLine($"  interval histogram: {Histogram(intervals, new[] { 2.0, 4, 6, 7, 9, 10, 12, 16, 24, 40 })}");
            // Speed per 100 ms window, to see whether the pace itself wavered.
            var windows = run.GroupBy(m => (int)((m.T - run[0].T) / 100)).OrderBy(g => g.Key)
                .Select(g => g.Count()).ToList();
            Console.WriteLine($"  moves per 100 ms: {string.Join(" ", windows)}");
        }
        if (!runs.Any(r => r.Count >= 20)) Console.WriteLine("no run of motion long enough to describe (20 moves or more)");
    }

    /// <summary>The agent's tick mechanism, reproduced: a precise 8 ms timer on one thread posting
    /// one tick at a time to an STA dispatcher and waiting for it, with the tick doing what the
    /// worker's does on the way to a move (read the pointer, open the input desktop). Measures the
    /// spacing of the ticks as they run on the dispatcher, for the priority the worker uses and
    /// for Send.</summary>
    /// <summary>The worker's tick mechanism in this process: the 8 ms clock posting one tick at a
    /// time to an STA dispatcher, at each priority in turn. With <paramref name="busy"/>, a second
    /// thread posts the launcher's requests as Serve does -- one no-op operation every 8 ms,
    /// awaited -- at Input beside an Input tick (the arrangement up to Oct 8 2026) and at Normal
    /// beside a Normal or Send tick (the arrangement since). A WPF background priority (Input and
    /// below Loaded) is run only when the dispatcher finds its Win32 queue empty, so two background
    /// producers can each find the other's posted message and wait for the message timer.</summary>
    public static void DispatcherCadence(bool inject = false, bool busy = false)
    {
        NativeMethods.GetCursorPos(out var home);
        foreach (var priority in new[] { DispatcherPriority.Input, DispatcherPriority.Normal, DispatcherPriority.Send })
        {
            var intervals = new List<double>();
            var work = new List<double>();
            var requestWaits = new List<double>();
            var requestPriority = priority == DispatcherPriority.Input ? DispatcherPriority.Input : DispatcherPriority.Normal;
            var thread = new Thread(() =>
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                using var stop = new CancellationTokenSource(4000);
                var pump = new Thread(() =>
                {
                    long last = 0; int step = 0;
                    try
                    {
                        using var cadence = new InputCadence();
                        while (!stop.IsCancellationRequested)
                        {
                            cadence.Wait();
                            dispatcher.InvokeAsync(() =>
                            {
                                long now = Stopwatch.GetTimestamp();
                                if (last != 0) intervals.Add((now - last) * 1000.0 / Stopwatch.Frequency);
                                last = now;
                                NativeMethods.GetCursorPos(out _);
                                using var desktop = DesktopApi.Open();
                                // The agent's own move: an absolute SendInput from the dispatcher
                                // tick, a pixel back and forth so the pointer stays put overall.
                                if (inject) NativeMethods.MoveCursorBy((++step & 1) == 0 ? 1 : -1, 0);
                                work.Add((Stopwatch.GetTimestamp() - now) * 1000.0 / Stopwatch.Frequency);
                            }, priority, stop.Token).Task.GetAwaiter().GetResult();
                        }
                    }
                    catch (OperationCanceledException) { }
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
                });
                var requests = busy ? new Thread(() =>
                {
                    try
                    {
                        using var cadence = new InputCadence();
                        while (!stop.IsCancellationRequested)
                        {
                            cadence.Wait();
                            long posted = Stopwatch.GetTimestamp();
                            dispatcher.InvokeAsync(() => requestWaits.Add((Stopwatch.GetTimestamp() - posted) * 1000.0 / Stopwatch.Frequency),
                                requestPriority, stop.Token).Task.GetAwaiter().GetResult();
                        }
                    }
                    catch (OperationCanceledException) { }
                }) : null;
                pump.Start();
                requests?.Start();
                Dispatcher.Run();
                pump.Join();
                requests?.Join();
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            Console.WriteLine($"dispatcher ticks at {priority}{(inject ? " with injected moves" : "")}{(busy ? $" with requests at {requestPriority}" : "")}: {intervals.Count} ticks; spacing ms {Describe(intervals)}; over 10 ms: {intervals.Count(i => i > 10)}, over 16 ms: {intervals.Count(i => i > 16)}; tick work ms max {work.DefaultIfEmpty(0).Max():F3}"
                + (busy ? $"; {requestWaits.Count} requests, queue wait ms {Describe(requestWaits)}" : ""));
        }
        if (inject) NativeMethods.SetCursorPos(home.X, home.Y);
    }

    /// <summary>
    /// Moves the real pointer the way the agent does, with no controller in the loop: a full
    /// stick push to the right for a second and back for a second, three times, from the shared
    /// mover at the agent's 8 ms cadence, through each of three injection methods in turn -- the
    /// absolute move the agent uses, a relative move, and SetCursorPos -- while another thread
    /// records what the pointer did. Watch the pointer during each pass. No clicks or keys.
    /// </summary>
    public static void PointerSweep()
    {
        NativeMethods.GetCursorPos(out var home);
        foreach (var (name, move) in new (string, Action<int, int>)[]
        {
            ("absolute move (what the agent sends)", NativeMethods.MoveCursorBy),
            ("relative move", (dx, dy) => NativeMethods.SendInputLocal(new[] { new NativeMethods.INPUT { type = NativeMethods.INPUT_MOUSE,
                u = new() { mi = new() { dx = dx, dy = dy, dwFlags = NativeMethods.MOUSEEVENTF_MOVE } } } })),
            ("SetCursorPos", (dx, dy) => { NativeMethods.GetCursorPos(out var p); NativeMethods.SetCursorPos(p.X + dx, p.Y + dy); }),
        })
        {
            Console.WriteLine($"--- {name}: watch the pointer ---");
            Thread.Sleep(1500);
            var moves = new List<(double T, int X, int Y)>();
            using var stop = new CancellationTokenSource();
            var sampler = new Thread(() =>
            {
                using var tick = new MillisecondTimer();
                var sw = Stopwatch.StartNew();
                int lx = int.MinValue, ly = int.MinValue;
                while (!stop.IsCancellationRequested)
                {
                    if (NativeMethods.GetCursorPos(out var p) && (p.X != lx || p.Y != ly)) { moves.Add((sw.Elapsed.TotalMilliseconds, p.X, p.Y)); lx = p.X; ly = p.Y; }
                    tick.Wait();
                }
            }) { IsBackground = true };
            sampler.Start();
            var mover = new StickPointer.Mover();
            using (var cadence = new InputCadence())
            {
                long last = Stopwatch.GetTimestamp();
                for (int pass = 0; pass < 6; pass++)
                {
                    short lx = (short)(pass % 2 == 0 ? 32767 : -32767);
                    for (int i = 0; i < 125; i++)
                    {
                        cadence.Wait();
                        long now = Stopwatch.GetTimestamp();
                        double dt = (now - last) / (double)Stopwatch.Frequency;
                        last = now;
                        var (dx, dy) = mover.Step(lx, 0, dt, .18, 1, 1.8, 1);
                        if (dx != 0 || dy != 0) move(dx, dy);
                    }
                }
            }
            Thread.Sleep(50);
            stop.Cancel(); sampler.Join();
            var intervals = new List<double>(); var steps = new List<double>();
            for (int i = 1; i < moves.Count; i++) { intervals.Add(moves[i].T - moves[i - 1].T); steps.Add(Math.Abs(moves[i].X - moves[i - 1].X)); }
            Console.WriteLine($"  {moves.Count} pointer changes; intervals ms {Describe(intervals)}; over 12 ms: {intervals.Count(i => i > 12)}; steps px {Describe(steps)}");
            NativeMethods.SetCursorPos(home.X, home.Y);
        }
        Console.WriteLine("Pointer put back where it was.");
    }

    /// <summary>
    /// Which pixel does Windows put the pointer on for an absolute, virtual-desktop move? Sends
    /// one move per column across the primary row and one per row down a column, aimed with
    /// the formula MoveCursorTo uses and with the alternative (the ceiling of x * 65536 / width),
    /// and reads the pointer back after each. The pointer flies across the screen for a few
    /// seconds and is put back. No clicks or keys.
    /// </summary>
    public static void AbsoluteMapping()
    {
        NativeMethods.GetCursorPos(out var home);
        int vx = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN), vy = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        int vw = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN), vh = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);
        Console.WriteLine($"virtual screen {vw}x{vh} at ({vx},{vy})");
        using var tick = new MillisecondTimer();
        foreach (var (name, aim) in new (string, Func<int, int, int>)[]
        {
            ("current: (x * 65535 + 32767) / (w - 1)", (x, w) => (int)((x * 65535.0 + 32767.0) / (w - 1))),
            ("ceiling: (x * 65536 + w - 1) / w", (x, w) => (int)((x * 65536L + w - 1) / w)),
            ("centre of 65536 range: (x * 65536 + 32768) / w", (x, w) => (int)((x * 65536L + 32768) / w)),
        })
        {
            int xMiss = 0, yMiss = 0, xChecked = 0, yChecked = 0; var missesAt = new List<string>();
            int y0 = home.Y;
            for (int x = 0; x < vw; x += 1)
            {
                Send(Math.Clamp(aim(x, vw), 0, 65535), Math.Clamp(aim(y0 - vy, vh), 0, 65535));
                tick.Wait();
                NativeMethods.GetCursorPos(out var p);
                xChecked++;
                if (p.X != vx + x) { xMiss++; if (missesAt.Count < 6) missesAt.Add($"x {vx + x}->{p.X}"); }
                if (p.Y != y0) { yMiss++; }
            }
            int x0 = home.X; int yMissCol = 0, yCheckedCol = 0;
            for (int y = 0; y < vh; y += 1)
            {
                Send(Math.Clamp(aim(x0 - vx, vw), 0, 65535), Math.Clamp(aim(y, vh), 0, 65535));
                tick.Wait();
                NativeMethods.GetCursorPos(out var p);
                yCheckedCol++;
                if (p.Y != vy + y) yMissCol++;
            }
            Console.WriteLine($"{name}: {xMiss} of {xChecked} columns landed on the wrong pixel, {yMiss} vertical wobbles during the row sweep; {yMissCol} of {yCheckedCol} rows wrong{(missesAt.Count > 0 ? "; e.g. " + string.Join(", ", missesAt) : "")}");
        }
        NativeMethods.SetCursorPos(home.X, home.Y);
        Console.WriteLine("Pointer put back where it was.");

        static void Send(int nx, int ny) => NativeMethods.SendInputLocal(new[] { new NativeMethods.INPUT { type = NativeMethods.INPUT_MOUSE, u = new() { mi = new()
            { dx = nx, dy = ny, dwFlags = NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE | NativeMethods.MOUSEEVENTF_VIRTUALDESK } } } });
    }

    /// <summary>
    /// The agent's standalone loop, reproduced in this process against the real pad: the same
    /// reader in quiet mode, the same machine profile, the same mapper, the same 8 ms pump
    /// posting one tick at a time to an STA dispatcher, with every SendInput recorded instead
    /// of sent -- so the real pointer never moves and the real agent is not fought. Reports the
    /// tick spacing, the cost of the capture and of the mapping, and the spacing of the moves
    /// the mapper asked for. Push the stick while it runs.
    /// </summary>
    public static void AgentLoop()
    {
        var ticks = new List<double>(); var captureMs = new List<double>(); var mapMs = new List<double>();
        var sends = new List<double>(); int sendCalls = 0, movePixels = 0;
        string? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var original = NativeMethods.InputSink;
            try
            {
                using var hid = new HidGamepadReader();
                hid.RefreshDirectDevices();
                hid.SetQuiet(true);
                var injector = new InputInjector();
                var profile = MachineInputSettings.Load();
                using var mapper = new SecureMapper(profile, injector);
                var gate = new NeutralInputGate();
                var previous = new NativeMethods.XINPUT_GAMEPAD[4];
                int xboxIndex = 0;
                NativeMethods.InputSink = inputs =>
                {
                    sendCalls++;
                    foreach (var i in inputs) if (i.type == NativeMethods.INPUT_MOUSE && (i.u.mi.dwFlags & NativeMethods.MOUSEEVENTF_MOVE) != 0)
                        sends.Add(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);
                    return (uint)inputs.Length;
                };
                using var stop = new CancellationTokenSource(6000);
                long last = 0;
                var pump = new Thread(() =>
                {
                    try
                    {
                        using var cadence = new InputCadence();
                        while (!stop.IsCancellationRequested)
                        {
                            cadence.Wait();
                            dispatcher.InvokeAsync(() =>
                            {
                                long t0 = Stopwatch.GetTimestamp();
                                if (last != 0) ticks.Add((t0 - last) * 1000.0 / Stopwatch.Frequency);
                                last = t0;
                                bool present = false;
                                NativeMethods.XINPUT_STATE xbox = default;
                                for (int i = 0; i < 4; i++)
                                {
                                    try
                                    {
                                        if (NativeMethods.XInputGetStateAny(i, out var state) != 0) continue;
                                        if (!state.Gamepad.Equals(previous[i])) xboxIndex = i;
                                        previous[i] = state.Gamepad;
                                        if (!present || i == xboxIndex) { xbox = state; present = true; }
                                    }
                                    catch (DllNotFoundException) { break; }
                                }
                                var snapshot = new AgentReply(Protocol.Version, true, present, xbox, hid.Snapshot(), true);
                                long t1 = Stopwatch.GetTimestamp();
                                captureMs.Add((t1 - t0) * 1000.0 / Stopwatch.Frequency);
                                if (!snapshot.XInputPresent && !snapshot.Hid.Present) mapper.ResetInput();
                                if (gate.Accept(snapshot))
                                {
                                    var pad = snapshot.Hid.Present ? snapshot.Hid.Pad : snapshot.Xbox.Gamepad;
                                    movePixels += Math.Abs(pad.sThumbLX) > 6000 || Math.Abs(pad.sThumbLY) > 6000 ? 1 : 0;
                                    mapper.Update(snapshot, Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);
                                }
                                mapMs.Add((Stopwatch.GetTimestamp() - t1) * 1000.0 / Stopwatch.Frequency);
                            }, DispatcherPriority.Input, stop.Token).Task.GetAwaiter().GetResult();
                        }
                    }
                    catch (OperationCanceledException) { }
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
                });
                pump.Start();
                Dispatcher.Run();
                pump.Join();
            }
            catch (Exception ex) { failure = ex.ToString(); }
            finally { NativeMethods.InputSink = original; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) { Console.WriteLine("agent loop failed: " + failure); return; }
        var sendGaps = new List<double>();
        for (int i = 1; i < sends.Count; i++) sendGaps.Add(sends[i] - sends[i - 1]);
        Console.WriteLine($"agent loop in this process: {ticks.Count} ticks; spacing ms {Describe(ticks)}; over 10 ms: {ticks.Count(t => t > 10)}, over 16 ms: {ticks.Count(t => t > 16)}");
        Console.WriteLine($"  capture ms {Describe(captureMs)}");
        Console.WriteLine($"  mapping ms {Describe(mapMs)}");
        Console.WriteLine($"  ticks with the stick pushed: {movePixels}; SendInput calls: {sendCalls}; pointer moves asked for: {sends.Count}; spacing of moves ms {Describe(sendGaps)}");
    }

    /// <summary>
    /// Watches the installed agent through the registry five times a second: every change of
    /// worker, desktop, mode, controller presence or error is printed as it happens, and each
    /// health period's tick timing follows it. Run it while repeating whatever misbehaves.
    /// Read-only; it moves nothing.
    /// </summary>
    public static void HealthWatch(int seconds)
    {
        Console.WriteLine($"Watching the agent for {seconds} s. Repeat the scenarios now.");
        string? lastKey = null, lastError = null, lastErrors = null; long lastTick = -1;
        var sw = Stopwatch.StartNew();
        using var tick = new MillisecondTimer();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(MachineInputSettings.RegistryPath);
            string stamp = $"{DateTime.Now:HH:mm:ss.f}";
            string? error = key?.GetValue("LastError") as string;
            if (error != lastError) { Console.WriteLine($"{stamp} LastError: {(error is null ? "(cleared)" : error)}"); lastError = error; }
            string? errors = key?.GetValue("Errors") as string;
            if (errors != lastErrors && errors is not null) { Console.WriteLine($"{stamp} Errors: {errors}"); lastErrors = errors; }
            var health = MachineInputSettings.Agent();
            if (health is null)
            {
                if (lastKey != "none") { Console.WriteLine($"{stamp} no worker health"); lastKey = "none"; }
            }
            else
            {
                string k = $"{health.ProcessId}|{health.Desktop}|{health.Mode}|{health.ControllerPresent}|{health.Ready}";
                if (k != lastKey) { Console.WriteLine($"{stamp} worker {health.ProcessId} on {health.Desktop}, {health.Mode}, controller={health.ControllerPresent} ready={health.Ready}"); lastKey = k; }
                if (health.Tick != lastTick)
                {
                    lastTick = health.Tick;
                    Console.WriteLine($"{stamp}   ticks={health.Ticks} mean={health.TickMeanMs:F2} max={health.TickMaxMs:F2} queue={health.QueueMeanMs:F2}/{health.QueueMaxMs:F2} work={health.WorkMaxMs:F2} wait={health.WaitMeanMs:F2} capture={health.CaptureMaxMs:F2} map={health.MapMaxMs:F2} send={health.SendMaxMs:F2} short={health.SendShort} xinputProbe={health.XInputProbeMaxMs:F2} slots={health.XInputSlots} suppressed={health.SuppressedNavigationEvents}");
                }
            }
            for (int i = 0; i < 200; i++) tick.Wait();
        }
    }

    private static string Describe(List<double> values)
    {
        if (values.Count == 0) return "none";
        var sorted = values.OrderBy(v => v).ToList();
        double mean = sorted.Average();
        double sd = Math.Sqrt(sorted.Average(v => (v - mean) * (v - mean)));
        return $"mean {mean:F2} sd {sd:F2} p50 {sorted[sorted.Count / 2]:F2} p95 {sorted[(int)(sorted.Count * .95)]:F2} p99 {sorted[(int)(sorted.Count * .99)]:F2} max {sorted[^1]:F2} min {sorted[0]:F2}";
    }

    private static string Histogram(List<double> values, double[] edges)
    {
        var parts = new List<string>();
        double low = double.NegativeInfinity;
        foreach (double edge in edges.Append(double.PositiveInfinity))
        {
            int n = values.Count(v => v > low && v <= edge);
            parts.Add($"{(double.IsNegativeInfinity(low) ? "<=" : low + "-")}{(double.IsPositiveInfinity(edge) ? "+" : edge.ToString())}:{n}");
            low = edge;
        }
        return string.Join(" ", parts);
    }

    /// <summary>A 1 ms wait on a high-resolution waitable timer: Thread.Sleep(1) is the system
    /// clock's 15.6 ms unless something raised the resolution.</summary>
    private sealed class MillisecondTimer : IDisposable
    {
        private readonly SafeWaitHandle _timer = CreateWaitableTimerEx(IntPtr.Zero, null, 0x2, 0x100002);
        public void Wait()
        {
            long due = -10000;
            SetWaitableTimer(_timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false);
            WaitForSingleObject(_timer, 100);
        }
        public void Dispose() => _timer.Dispose();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeWaitHandle CreateWaitableTimerEx(IntPtr attributes, string? name, uint flags, uint access);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long due, int period, IntPtr callback, IntPtr arg, bool resume);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);
    }
}

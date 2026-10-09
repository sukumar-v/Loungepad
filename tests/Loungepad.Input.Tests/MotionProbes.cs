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
    public static void PointerTrace(int seconds)
    {
        var moves = new List<(double T, int X, int Y)>();
        var sw = Stopwatch.StartNew();
        int lastX = int.MinValue, lastY = int.MinValue;
        using var tick = new MillisecondTimer();
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
        foreach (var run in runs.Where(r => r.Count >= 20))
        {
            var intervals = new List<double>();
            var steps = new List<double>();
            for (int i = 1; i < run.Count; i++)
            {
                intervals.Add(run[i].T - run[i - 1].T);
                steps.Add(Math.Sqrt(Math.Pow(run[i].X - run[i - 1].X, 2) + Math.Pow(run[i].Y - run[i - 1].Y, 2)));
            }
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
    public static void DispatcherCadence()
    {
        foreach (var priority in new[] { DispatcherPriority.Input, DispatcherPriority.Normal, DispatcherPriority.Send })
        {
            var intervals = new List<double>();
            var work = new List<double>();
            var thread = new Thread(() =>
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                using var stop = new CancellationTokenSource(4000);
                var pump = new Thread(() =>
                {
                    long last = 0;
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
                                work.Add((Stopwatch.GetTimestamp() - now) * 1000.0 / Stopwatch.Frequency);
                            }, priority, stop.Token).Task.GetAwaiter().GetResult();
                        }
                    }
                    catch (OperationCanceledException) { }
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
                });
                pump.Start();
                Dispatcher.Run();
                pump.Join();
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            Console.WriteLine($"dispatcher ticks at {priority}: {intervals.Count} ticks; spacing ms {Describe(intervals)}; over 10 ms: {intervals.Count(i => i > 10)}, over 16 ms: {intervals.Count(i => i > 16)}; tick work ms max {work.DefaultIfEmpty(0).Max():F3}");
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

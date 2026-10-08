using System.Diagnostics;
using Loungepad.Interop;
using Loungepad.Input;
using Loungepad.Services;

internal static class InputTimingProbe
{
    // Read-only hardware timing. Never prints controller reports or injects input.
    public static void Run()
    {
        for (int slot = 0; slot < 4; slot++)
        {
            int index = slot;
            Measure($"XInput slot {slot}", () => NativeMethods.XInputGetStateAny(index, out _), 64);
        }
        using var hid = new HidGamepadReader();
        hid.RefreshDirectDevices();
        Measure("HID device discovery", hid.RefreshDirectDevices, 32);
        Measure("Thread.Sleep(8)", () => Thread.Sleep(8), 100);
        Measure("Task.Delay(8)", () => Task.Delay(8).GetAwaiter().GetResult(), 100);
        using (var cadence = new InputCadence()) Measure("High-resolution input cadence", () => cadence.Wait(), 100);
        using (var cadence = new InputCadence())
        {
            int zeroSteps = 0;
            long previous = Environment.TickCount64;
            for (int i = 0; i < 100; i++)
            {
                cadence.Wait();
                long current = Environment.TickCount64;
                if (current == previous) zeroSteps++;
                previous = current;
            }
            Console.WriteLine($"TickCount64 movement timing: {zeroSteps}/100 ticks would have zero dt at 125 Hz");
        }
        if (timeBeginPeriod(1) == 0)
        {
            try
            {
                Measure("Thread.Sleep(8), 1ms resolution", () => Thread.Sleep(8), 100);
                Measure("Task.Delay(8), 1ms resolution", () => Task.Delay(8).GetAwaiter().GetResult(), 100);
            }
            finally { timeEndPeriod(1); }
        }
    }

    private static void Measure(string label, Action action, int count)
    {
        var samples = new double[count];
        for (int i = 0; i < count; i++)
        {
            long start = Stopwatch.GetTimestamp();
            action();
            samples[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        Array.Sort(samples);
        Console.WriteLine($"{label}: mean={samples.Average():F3}ms; p95={samples[(int)(count * .95)]:F3}ms; max={samples[^1]:F3}ms");
    }

    [System.Runtime.InteropServices.DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint milliseconds);
    [System.Runtime.InteropServices.DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint milliseconds);
}

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Loungepad.Input;

// A process-local timer: no system timer-resolution changes and no busy spinning.
// Keep an absolute schedule so device/IPC work does not add to every polling interval.
internal sealed class InputCadence : IDisposable
{
    private readonly SafeWaitHandle _timer;
    private readonly long _interval = Stopwatch.Frequency * 8 / 1000;
    private long _next;

    public InputCadence()
    {
        _timer = CreateWaitableTimerEx(IntPtr.Zero, null, 0x2, 0x100002);
        if (_timer.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        _next = Stopwatch.GetTimestamp() + _interval;
    }

    public void Wait(EventWaitHandle? wake = null)
    {
        long now = Stopwatch.GetTimestamp();
        if (now >= _next) { _next = now + _interval; return; }
        long due = -Math.Max(1, (long)((_next - now) * (10000000.0 / Stopwatch.Frequency)));
        if (!SetWaitableTimer(_timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        uint result = wake is null ? WaitForSingleObject(_timer, uint.MaxValue)
            : WaitForMultipleObjects(2, new[] { _timer.DangerousGetHandle(), wake.SafeWaitHandle.DangerousGetHandle() }, false, uint.MaxValue);
        GC.KeepAlive(wake);
        GC.KeepAlive(_timer);
        if (result == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (result == 0)
        {
            long next = _next + _interval, completed = Stopwatch.GetTimestamp();
            _next = next > completed ? next : completed + _interval;
        }
        // An output event can wake IPC early without moving the next polling deadline.
    }

    public void Dispose() => _timer.Dispose();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateWaitableTimerEx(IntPtr attributes, string? name, uint flags, uint access);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long due, int period, IntPtr callback, IntPtr arg, bool resume);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForMultipleObjects(uint count, IntPtr[] handles, bool all, uint milliseconds);
}

using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Loungepad.InputAgent;

// Windows' built-in controller navigation emits VK_GAMEPAD_* keyboard events in
// addition to the physical XInput/HID readings. Consume only that virtual-key range
// while Loungepad is mapping the controller to a mouse. Never retain or log keys.
//
// The hook lives on a thread of its own that does nothing but pump messages for it. A
// low-level hook is called on the thread that installed it, and Windows gives that thread a
// few hundred milliseconds (LowLevelHooksTimeout) to answer; a thread that does not pump in
// time is skipped, and after that the hook is removed silently, for good, with nothing to say
// so. The worker's dispatcher thread is the one that shows the secure keyboard -- a WPF
// window, whose first show is easily that long -- so the hook cannot share it.
internal sealed class GamepadNavigationFilter : IDisposable
{
    private readonly Func<bool> _enabled;
    private readonly HookCallback _callback;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _installed = new();
    private IntPtr _hook;
    private uint _threadId;
    private Exception? _failure;
    private long _blocked;
    public long BlockedCount => Interlocked.Read(ref _blocked);

    public GamepadNavigationFilter(Func<bool> enabled)
    {
        _enabled = enabled;
        _callback = Filter;
        _thread = new Thread(Pump) { IsBackground = true, Name = "Loungepad gamepad navigation filter" };
        _thread.Start();
        _installed.Wait();
        if (_failure is not null) throw _failure;
    }

    private void Pump()
    {
        _threadId = GetCurrentThreadId();
        _hook = SetWindowsHookEx(13, _callback, GetModuleHandle(null), 0); // WH_KEYBOARD_LL, current desktop
        if (_hook == IntPtr.Zero) _failure = new Win32Exception(Marshal.GetLastWin32Error());
        _installed.Set();
        if (_hook == IntPtr.Zero) return;
        try
        {
            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        finally
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    internal static bool ShouldSuppress(int code, uint virtualKey, bool enabled) =>
        code >= 0 && enabled && virtualKey is >= 0xC3 and <= 0xDA; // VK_GAMEPAD_A through RIGHT_THUMBSTICK_LEFT

    private IntPtr Filter(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && ShouldSuppress(code, unchecked((uint)Marshal.ReadInt32(data)), _enabled()))
        {
            Interlocked.Increment(ref _blocked);
            return new IntPtr(1);
        }
        return CallNextHookEx(IntPtr.Zero, code, message, data);
    }

    public void Dispose()
    {
        if (_thread.IsAlive && _threadId != 0)
        {
            PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _thread.Join(2000);
        }
        GC.KeepAlive(_callback);
    }

    private const uint WM_QUIT = 0x0012;
    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }
    private delegate IntPtr HookCallback(int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int type, HookCallback callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG message, IntPtr window, uint first, uint last);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG message);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? module);
}

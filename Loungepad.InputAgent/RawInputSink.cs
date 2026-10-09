using System.Windows.Interop;
using Loungepad.Interop;
using Loungepad.Services;

namespace Loungepad.InputAgent;

/// <summary>
/// A message-only window that takes Raw Input for the HID reader. An Xbox pad's HID interface
/// (the "IG_" device) delivers its reports only this way: a direct ReadFile on it opens and never
/// returns a report (--hid-probe xbox, Oct 9 2026: fourteen minutes of use, XInput saw every
/// button, trigger and the full stick, the direct read saw nothing), and XInput itself reports
/// every axis at rest behind a locked session. Used by the Winlogon worker only, on its
/// dispatcher thread, which pumps this window's messages. While the registration is held Windows
/// counts every registered pad's reports as user input, a DualSense's 250 a second included, so
/// on a locked PC with a Sony pad attached the display stays awake: the price of an Xbox pad
/// working on the sign-in screen, paid only there.
/// </summary>
internal sealed class RawInputSink : IDisposable
{
    private static readonly IntPtr HWND_MESSAGE = new(-3);
    private readonly HwndSource _source;
    private readonly HidGamepadReader _hid;
    public IntPtr Handle => _source.Handle;

    public RawInputSink(HidGamepadReader hid)
    {
        _hid = hid;
        _source = new HwndSource(new HwndSourceParameters("Loungepad input sink")
        { ParentWindow = HWND_MESSAGE, WindowStyle = 0, ExtendedWindowStyle = 0, Width = 0, Height = 0 });
        _source.AddHook(Hook);
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Never marked handled: WM_INPUT has to reach DefWindowProc so the system releases the buffer.
        if (msg == HidNative.WM_INPUT) _hid.OnInput(lParam);
        else if (msg == HidNative.WM_INPUT_DEVICE_CHANGE) _hid.OnDeviceChange(wParam, lParam);
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        _source.RemoveHook(Hook);
        _source.Dispose();
    }
}

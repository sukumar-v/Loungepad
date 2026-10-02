using System.Runtime.InteropServices;
using Loungepad.Interop;

namespace Loungepad.Services;

/// <summary>
/// When the keyboard and the mouse were last used, by kind: a press (a key down, a mouse button
/// down), the wheel, and movement that adds up to something. Read off Raw Input on the main
/// window with INPUTSINK, so it sees the whole desktop whichever window is in front.
///
/// Rest mode asks two questions and GetLastInputInfo answers neither. Whether somebody is still
/// there is "any deliberate activity", and whether somebody wants the screen back is "a press":
/// a mouse nudged on the desk, a pad's stick drifting through Steam Input as mouse movement, must
/// not wake a dark room, and the user said so. And GetLastInputInfo is pinned at zero whenever a
/// Sony pad is attached, because the launcher's gamepad Raw Input registration makes Windows count
/// the pad's report stream as input (see HidGamepadReader.SetQuiet). This class is what replaced
/// it. Movement counts as activity only once it adds up to <see cref="MoveThreshold"/> counts
/// within a second: a sensor's jitter is a count or two now and then, a hand on the mouse is
/// hundreds.
///
/// Mouse and keyboard registrations change nothing about what Windows counts: those devices are
/// input already. The stamps are read from the pad thread and the UI thread; Volatile keeps
/// the longs whole.
/// </summary>
internal sealed class UserInputWatch
{
    /// <summary>Mouse counts within one second that make movement count as somebody being there.</summary>
    public const int MoveThreshold = 24;

    private long _pressTick = long.MinValue / 2;
    private long _wheelTick = long.MinValue / 2;
    private long _moveTick = long.MinValue / 2;
    private long _moveWindowStart;
    private int _moveAccum;
    private byte[] _buffer = new byte[256];
    private bool _registered;

    /// <summary>Milliseconds since a key or a mouse button went down.</summary>
    public long PressAgeMs => Environment.TickCount64 - Volatile.Read(ref _pressTick);

    /// <summary>Milliseconds since any deliberate use: a press, the wheel, or movement past the threshold.</summary>
    public long ActivityAgeMs
    {
        get
        {
            long now = Environment.TickCount64;
            long last = Math.Max(Volatile.Read(ref _pressTick), Math.Max(Volatile.Read(ref _wheelTick), Volatile.Read(ref _moveTick)));
            return now - last;
        }
    }

    /// <summary>Ask for every mouse and keyboard event, whichever window is in front. On the window thread.</summary>
    public void Register(IntPtr hwnd)
    {
        if (_registered) return;
        var devices = new[]
        {
            new HidNative.RAWINPUTDEVICE { usUsagePage = HidNative.USAGE_PAGE_GENERIC, usUsage = HidNative.USAGE_MOUSE, dwFlags = HidNative.RIDEV_INPUTSINK, hwndTarget = hwnd },
            new HidNative.RAWINPUTDEVICE { usUsagePage = HidNative.USAGE_PAGE_GENERIC, usUsage = HidNative.USAGE_KEYBOARD, dwFlags = HidNative.RIDEV_INPUTSINK, hwndTarget = hwnd },
        };
        if (!HidNative.RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<HidNative.RAWINPUTDEVICE>()))
        {
            Log.Info($"Input watch: RegisterRawInputDevices failed ({Marshal.GetLastWin32Error()}); rest will not see the keyboard or mouse");
            return;
        }
        _registered = true;
    }

    /// <summary>
    /// Read a WM_INPUT's data once, for every reader: the buffer and its size, or a size of 0.
    /// The buffer is reused, so it is only good until the next call.
    /// </summary>
    public (byte[] Buffer, uint Size) Read(IntPtr hRawInput)
    {
        uint size = 0;
        if (HidNative.GetRawInputData(hRawInput, HidNative.RID_INPUT, null, ref size, HidNative.RawInputHeaderSize) != 0 || size == 0) return (_buffer, 0);
        if (_buffer.Length < size) _buffer = new byte[size];
        if (HidNative.GetRawInputData(hRawInput, HidNative.RID_INPUT, _buffer, ref size, HidNative.RawInputHeaderSize) == unchecked((uint)-1)) return (_buffer, 0);
        return (_buffer, size);
    }

    /// <summary>A mouse or keyboard WM_INPUT already read. Anything else is ignored.</summary>
    public void OnInputData(byte[] buffer, uint size)
    {
        int header = (int)HidNative.RawInputHeaderSize;
        uint type = BitConverter.ToUInt32(buffer, 0);
        long now = Environment.TickCount64;
        if (type == HidNative.RIM_TYPEKEYBOARD)
        {
            if (size < header + 16) return;
            // RAWKEYBOARD: MakeCode, Flags, Reserved, VKey (ushorts), Message, ExtraInformation.
            ushort flags = BitConverter.ToUInt16(buffer, header + 2);
            ushort vkey = BitConverter.ToUInt16(buffer, header + 6);
            // Bit 0 set is the key coming up; 0xFF is the fake key that escorts an extended one.
            if ((flags & 1) == 0 && vkey != 0xFF) Volatile.Write(ref _pressTick, now);
            return;
        }
        if (type != HidNative.RIM_TYPEMOUSE || size < header + 24) return;
        // RAWMOUSE: usFlags, (padding), usButtonFlags, usButtonData, ulRawButtons, lLastX, lLastY, ulExtraInformation.
        ushort buttons = BitConverter.ToUInt16(buffer, header + 4);
        int dx = BitConverter.ToInt32(buffer, header + 12);
        int dy = BitConverter.ToInt32(buffer, header + 16);
        const ushort downs = 0x0001 | 0x0004 | 0x0010 | 0x0040 | 0x0100;   // left, right, middle, 4 and 5 down
        const ushort wheels = 0x0400 | 0x0800;
        if ((buttons & downs) != 0) Volatile.Write(ref _pressTick, now);
        if ((buttons & wheels) != 0) Volatile.Write(ref _wheelTick, now);
        int mag = Math.Abs(dx) + Math.Abs(dy);
        if (mag == 0) return;
        if (now - _moveWindowStart > 1000) { _moveWindowStart = now; _moveAccum = 0; }
        _moveAccum += mag;
        if (_moveAccum >= MoveThreshold) Volatile.Write(ref _moveTick, now);
    }
}

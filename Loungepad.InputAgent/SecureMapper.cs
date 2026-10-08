using System.Diagnostics;
using Loungepad.Input;
using Loungepad.Interop;
using Loungepad.Models;
using Loungepad.Services;
using static Loungepad.Interop.NativeMethods;

namespace Loungepad.InputAgent;

internal sealed class SecureMapper : IDisposable
{
    private readonly InputProfile _profile;
    private readonly InputInjector _injector;
    private readonly AppSettings _settings;
    private readonly TouchpadGestures _touch = new();
    private readonly StickPointer.Mover _mover = new();
    private readonly Dictionary<ushort, double> _repeat = new();
    private KeyboardWindow? _keyboard;
    private Process? _external;
    private NativeMethods.XINPUT_GAMEPAD _lastX, _lastH;
    private bool _useHid, _left, _right, _toggleFired, _lt;
    private ushort _previous;
    private double _lastTick, _toggleAt = -1, _wheel, _hWheel;

    public SecureMapper(InputProfile profile, InputInjector injector)
    {
        _profile = profile; _injector = injector;
        _settings = new()
        {
            TouchpadSensitivity = profile.TouchpadSensitivity, TouchpadTapToClick = profile.TouchpadTapToClick,
            TouchpadTapDrag = profile.TouchpadTapDrag, TouchpadNaturalScroll = profile.TouchpadNaturalScroll,
            TouchpadScrollSpeed = profile.TouchpadScrollSpeed,
        };
        KeyboardWindow.Probe = () => default;
        _touch.PointerMoved = () => _keyboard?.SetInputMode("pointer");
        _touch.Clicking = () => _keyboard?.TargetClicked();
    }

    /// <summary>
    /// The whole pad, on a desktop where this process is the only mapper: the Winlogon desktop,
    /// and Default with no launcher connected. Buttons, keyboard, pointer, wheel and touchpad.
    /// `now` is milliseconds on a fine clock (Stopwatch), not TickCount.
    /// </summary>
    public void Update(AgentReply snapshot, double now)
    {
        double dt = Dt(now);
        if (SelectPad(snapshot) is not { } pad) { Reset(); return; }
        ushort pressed = (ushort)(pad.wButtons & ~_previous);
        ushort released = (ushort)(_previous & ~pad.wButtons);
        _previous = pad.wButtons;
        _keyboard?.SetLayout(_useHid ? snapshot.Hid.Layout : "xbox");

        ushort toggle = ButtonMask(_profile.KeyboardToggle);
        if ((pressed & toggle) != 0)
        {
            _toggleAt = now; _toggleFired = false;
            if (_profile.KeyboardToggleMode == "Press") { ToggleKeyboard(); _toggleFired = true; }
        }
        if (_toggleAt >= 0 && !_toggleFired && (pad.wButtons & toggle) != 0 && now - _toggleAt >= _profile.KeyboardToggleHoldMs)
        { ToggleKeyboard(); _toggleFired = true; }
        if ((released & toggle) != 0) _toggleAt = -1;
        pressed &= (ushort)~toggle;
        ushort held = (ushort)(pad.wButtons & ~toggle);

        bool keyboardVisible = _keyboard?.IsVisible == true;
        bool keyboardArmed = keyboardVisible && _keyboard!.Armed;
        if (keyboardVisible)
        {
            if (keyboardArmed && (_left || _right)) ResetMouse();
            foreach (var (mask, action) in KeyboardButtons)
            {
                if (!keyboardArmed && action is not ("Up" or "Down" or "Left" or "Right" or "Close")) continue;
                if ((pressed & mask) != 0) { Key(action); _repeat[mask] = now + _profile.RepeatDelayMs; }
                else if ((pad.wButtons & mask) != 0 && action is "Up" or "Down" or "Left" or "Right" or "Backspace"
                    && _repeat.TryGetValue(mask, out double due) && now >= due)
                { Key(action); _repeat[mask] = now + _profile.RepeatIntervalMs; }
                if ((released & mask) != 0) _repeat.Remove(mask);
            }
            bool lt = pad.bLeftTrigger >= StickPointer.TriggerThreshold;
            if (lt && !_lt && keyboardArmed) _keyboard!.ToggleLayer();
            _lt = lt;
        }
        if (!keyboardArmed)
        {
            if (_profile.MouseEnabled)
            {
                Click(held, pressed, ButtonMask(_profile.LeftClick), ref _left, MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP);
                Click(keyboardVisible ? (ushort)(held & ~XINPUT_GAMEPAD_B) : held, pressed, ButtonMask(_profile.RightClick), ref _right, MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP);
            }
            if (!keyboardVisible) foreach (var (mask, key) in new[] { (XINPUT_GAMEPAD_DPAD_UP, VK_UP), (XINPUT_GAMEPAD_DPAD_DOWN, VK_DOWN),
                (XINPUT_GAMEPAD_DPAD_LEFT, VK_LEFT), (XINPUT_GAMEPAD_DPAD_RIGHT, VK_RIGHT), (XINPUT_GAMEPAD_START, VK_RETURN) })
                if ((pressed & mask) != 0) SendVirtualKey(key);
        }
        if (_profile.MouseEnabled) Pointer(pad, dt, move: true, wheel: true, _useHid && _profile.TouchpadMouse ? snapshot.Hid : null, now);
        else { _mover.Reset(); _wheel = _hWheel = 0; _touch.Reset(); }
    }

    /// <summary>
    /// Default with the launcher connected: only the pointer and the wheel, as the launcher's
    /// policy asks for them this tick. The buttons, the keyboard and the touchpad stay the
    /// launcher's -- it knows about games, overlays and its own keyboard; this process only knows
    /// how to move a pointer smoothly, and does it here from the same reading and the same
    /// formula as on a secure desktop, so a push feels the same everywhere.
    /// </summary>
    public void MovePointer(AgentReply snapshot, double now, bool move, bool wheel)
    {
        double dt = Dt(now);
        if (SelectPad(snapshot) is not { } pad || !_profile.MouseEnabled) { _mover.Reset(); _wheel = _hWheel = 0; return; }
        Pointer(pad, dt, move, wheel, touch: null, now);
    }

    private double Dt(double now)
    {
        double dt = _lastTick == 0 ? .008 : Math.Clamp((now - _lastTick) / 1000.0, 0, .05);
        _lastTick = now;
        return dt;
    }

    /// <summary>Which pad is driving: the one that moved last. Null with nothing attached.</summary>
    private NativeMethods.XINPUT_GAMEPAD? SelectPad(AgentReply snapshot)
    {
        bool hm = snapshot.Hid.Present && (!snapshot.Hid.Pad.Equals(_lastH) || snapshot.Hid.TouchDx != 0 || snapshot.Hid.TouchDy != 0 || snapshot.Hid.TouchClick);
        bool xm = snapshot.XInputPresent && !snapshot.Xbox.Gamepad.Equals(_lastX);
        _lastX = snapshot.Xbox.Gamepad; _lastH = snapshot.Hid.Pad;
        if (hm && !xm) _useHid = true; else if (xm && !hm) _useHid = false;
        if (_useHid && !snapshot.Hid.Present) _useHid = false;
        if (!_useHid && !snapshot.XInputPresent) _useHid = true;
        if (!snapshot.XInputPresent && !snapshot.Hid.Present) return null;
        return _useHid ? snapshot.Hid.Pad : snapshot.Xbox.Gamepad;
    }

    private void Pointer(in XINPUT_GAMEPAD pad, double dt, bool move, bool wheel, HidPadSnapshot? touch, double now)
    {
        double boost = ButtonHeld(pad, _profile.BoostButton) ? _profile.BoostMultiplier : 1;
        if (move)
        {
            var (dx, dy) = _mover.Step(pad.sThumbLX, pad.sThumbLY, dt, _profile.Deadzone, _profile.Sensitivity, _profile.AccelExponent, boost);
            if (dx != 0 || dy != 0)
            {
                _keyboard?.SetInputMode("pointer");
                // Absolute injected movement, like the launcher's: Windows applies no pointer
                // acceleration to it, so the curve in StickPointer is the whole of the feel.
                MoveCursorBy(dx, dy);
            }
        }
        else _mover.Reset();
        if (wheel)
        {
            double ry = pad.sThumbRY / 32767.0, rx = pad.sThumbRX / 32767.0;
            // Whole notches, and whichever axis is pushed further wins, exactly as the launcher
            // does it: a diagonal nudge never scrolls both ways.
            if (Math.Abs(ry) >= Math.Abs(rx))
            {
                _hWheel = 0;
                _wheel = StickPointer.Scroll(ry, _profile.Deadzone, dt, boost, _wheel, n => _injector.Mouse(MOUSEEVENTF_WHEEL, data: n * 120));
            }
            else
            {
                _wheel = 0;
                _hWheel = StickPointer.Scroll(rx, _profile.Deadzone, dt, boost, _hWheel, n => _injector.Mouse(MOUSEEVENTF_HWHEEL, data: n * 120));
            }
        }
        else { _wheel = 0; _hWheel = 0; }
        if (touch is { } h) _touch.Update(new(h.TouchDx, h.TouchDy, h.TouchSpread, h.TouchFingers, h.TouchClick), _settings, (long)now, dt);
        else _touch.Reset();
    }

    private void Click(ushort held, ushort pressed, ushort mask, ref bool down, uint press, uint release)
    {
        bool next = (held & mask) != 0;
        if (next && !down && (pressed & mask) == 0) return; // keyboard's closing press stays spent
        if (next == down) return;
        _injector.Mouse(next ? press : release); down = next;
    }

    private void ToggleKeyboard()
    {
        ResetMouse();
        if (_profile.KeyboardApp == "Builtin")
        {
            if (_keyboard?.IsVisible == true) { _keyboard.Hide(); return; }
            if (_keyboard is null)
            {
                _keyboard = new KeyboardWindow(secureInput: true);
                _keyboard.CloseRequested += () => _keyboard.Hide();
            }
            var displays = Displays();
            var display = displays.FirstOrDefault(d => d.DeviceName == _profile.Display) ?? displays.FirstOrDefault(d => d.IsPrimary)
                ?? displays.FirstOrDefault() ?? new("", "Display", 0, 0, GetSystemMetrics(0), GetSystemMetrics(1), true);
            _keyboard.ShowOn(display, _profile.KeyboardScale, new(false, _profile.FunctionKeys, _profile.NavKeys, _profile.Numpad, _profile.Modifiers));
            _keyboard.SetInputMode("pad");
            return;
        }
        if (_external?.HasExited == false) { _external.Kill(); _external.Dispose(); _external = null; return; }
        string path = _profile.KeyboardApp == "Osk" ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "osk.exe")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), @"microsoft shared\ink\TabTip.exe");
        // Fixed Windows keyboard paths only. No shell, arguments, user files or credentials.
        _external = Process.Start(new ProcessStartInfo(path) { UseShellExecute = false });
    }

    private static List<DisplayInfo> Displays()
    {
        var displays = new List<DisplayInfo>();
        var device = new DISPLAY_DEVICE { cb = (uint)System.Runtime.InteropServices.Marshal.SizeOf<DISPLAY_DEVICE>() };
        for (uint i = 0; EnumDisplayDevices(null, i, ref device, 0); i++)
        {
            var mode = new DEVMODE { dmSize = (ushort)System.Runtime.InteropServices.Marshal.SizeOf<DEVMODE>() };
            if ((device.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0 || !EnumDisplaySettings(device.DeviceName, ENUM_CURRENT_SETTINGS, ref mode)) continue;
            displays.Add(new(device.DeviceName, device.DeviceString, mode.dmPositionX, mode.dmPositionY, (int)mode.dmPelsWidth, (int)mode.dmPelsHeight, (device.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0));
        }
        return displays;
    }

    private void Key(string action)
    {
        if (_keyboard?.IsVisible != true) return;
        switch (action)
        {
            case "Up": case "Down": case "Left": case "Right": _keyboard.SetInputMode("pad"); _keyboard.Move(action); break;
            case "Press": _keyboard.Press(); break;
            case "Close": if (!_keyboard.LeaveOptions()) _keyboard.Hide(); break;
            case "Backspace": _keyboard.Backspace(); break;
            case "Space": _keyboard.Space(); break;
            case "Commit": _keyboard.Commit(); break;
            case "Shift": _keyboard.ToggleShift(); break;
            case "CaretLeft": _keyboard.CaretLeft(); break;
            case "CaretRight": _keyboard.CaretRight(); break;
        }
    }
    private void ResetMouse() { _touch.Reset(); _injector.Release(); _left = _right = false; }
    private void Reset() { ResetMouse(); _previous = 0; _lt = false; _repeat.Clear(); _mover.Reset(); _wheel = _hWheel = 0; }
    public void ResetInput() => Reset();
    public void Relinquish()
    {
        Reset(); _keyboard?.Hide();
        if (_external?.HasExited == false) _external.Kill();
        _external?.Dispose(); _external = null;
    }
    public void Dispose() { Reset(); _keyboard?.Close(); if (_external?.HasExited == false) _external.Kill(); _external?.Dispose(); }

    private static bool ButtonHeld(XINPUT_GAMEPAD pad, string button) => button switch
    { "LT" => pad.bLeftTrigger >= StickPointer.TriggerThreshold, "RT" => pad.bRightTrigger >= StickPointer.TriggerThreshold, _ => (pad.wButtons & ButtonMask(button)) != 0 };
    internal static ushort ButtonMask(string button) => button switch
    {
        "A" => XINPUT_GAMEPAD_A, "B" => XINPUT_GAMEPAD_B, "X" => XINPUT_GAMEPAD_X, "Y" => XINPUT_GAMEPAD_Y,
        "LB" => XINPUT_GAMEPAD_LEFT_SHOULDER, "RB" => XINPUT_GAMEPAD_RIGHT_SHOULDER,
        "LS" => XINPUT_GAMEPAD_LEFT_THUMB, "RS" => XINPUT_GAMEPAD_RIGHT_THUMB,
        "Back" or "View" => XINPUT_GAMEPAD_BACK, "Start" or "Menu" => XINPUT_GAMEPAD_START,
        "Guide" => XINPUT_GAMEPAD_GUIDE, _ => 0,
    };
    private static readonly (ushort Mask, string Action)[] KeyboardButtons =
    {
        (XINPUT_GAMEPAD_DPAD_UP, "Up"), (XINPUT_GAMEPAD_DPAD_DOWN, "Down"), (XINPUT_GAMEPAD_DPAD_LEFT, "Left"), (XINPUT_GAMEPAD_DPAD_RIGHT, "Right"),
        (XINPUT_GAMEPAD_A, "Press"), (XINPUT_GAMEPAD_B, "Close"), (XINPUT_GAMEPAD_X, "Backspace"), (XINPUT_GAMEPAD_Y, "Space"),
        (XINPUT_GAMEPAD_LEFT_SHOULDER, "CaretLeft"), (XINPUT_GAMEPAD_RIGHT_SHOULDER, "CaretRight"), (XINPUT_GAMEPAD_LEFT_THUMB, "Shift"), (XINPUT_GAMEPAD_START, "Commit"),
    };
}

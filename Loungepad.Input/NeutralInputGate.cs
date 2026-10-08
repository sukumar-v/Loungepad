using Loungepad.Interop;

namespace Loungepad.Input;

internal sealed class NeutralInputGate
{
    private bool _armed;
    public bool Armed => _armed;
    public void Reset() => _armed = false;
    public bool Accept(AgentReply snapshot)
    {
        if (!snapshot.XInputPresent && !snapshot.Hid.Present) { Reset(); return false; }
        if (!_armed) _armed = Neutral(snapshot.Xbox.Gamepad) && Neutral(snapshot.Hid.Pad)
            && !snapshot.Hid.TouchClick && snapshot.Hid.TouchFingers == 0;
        return _armed;
    }
    internal static bool Neutral(NativeMethods.XINPUT_GAMEPAD pad) => pad.wButtons == 0 && pad.bLeftTrigger < 40 && pad.bRightTrigger < 40
        && Math.Abs((int)pad.sThumbLX) < 8000 && Math.Abs((int)pad.sThumbLY) < 8000
        && Math.Abs((int)pad.sThumbRX) < 8000 && Math.Abs((int)pad.sThumbRY) < 8000;
}

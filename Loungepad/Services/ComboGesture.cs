namespace Loungepad.Services;

/// <summary>
/// The menu combo as a gesture, one <see cref="Update"/> per poll. Two shapes:
///
///  • Tap and hold (the default). A tap opens the Power Wheel, fired on the release, so it costs
///    no more than the tap itself. Holding past <see cref="HoldMs"/> shows or hides Loungepad (the
///    in-game menu while a game runs), fired while still held so the hold answers at once, and the
///    release after it does nothing.
///  • Double tap (the old one). A tap shows or hides Loungepad, a double tap opens the Power Wheel.
///    The single tap has to wait out the double-tap window, which is the lag this mode is known
///    for, and a slow second tap reads as two singles -- why tap-and-hold replaced it.
///
/// Kept apart from the pad loop so the harness can replay presses against it with a fake clock.
/// </summary>
internal sealed class ComboGesture
{
    public enum Fire { None, Tap, DoubleTap, Hold }

    // Second combo tap within this window opens the radial. Measured from the first press, and
    // the combo is two buttons (LS+RS by default), so the budget has to cover holding the first
    // tap, releasing BOTH buttons, and pressing again. 320ms did not: most double taps missed,
    // and the deferred single tap then opened the launcher instead.
    public const int DoubleTapMs = 550;
    /// <summary>
    /// How long a hold is. Long enough that a relaxed tap never reaches it, short enough to feel
    /// like an answer. Well under the ~6 s it takes an Xbox pad to switch itself off, which is the
    /// other thing a held Xbox button does.
    /// </summary>
    public const int HoldMs = 500;

    /// <summary>The mode of the last update; null until the first, which is not a switch.</summary>
    private bool? _tapHold;
    private bool _down;
    private long _downAt = -1;
    private bool _holdFired;
    private long _firstTapAt = -1;
    private bool _pendingTap;

    /// <summary>What the combo does this poll. <paramref name="rising"/> is true on the poll it
    /// became held -- the pad loop swallows a keyboard chord inside the combo on that edge.</summary>
    public Fire Update(bool held, long now, bool tapHold, out bool rising)
    {
        rising = false;

        // The mode changed under a gesture (a settings save). Start clean, and if the combo is down
        // right now treat that press as spent, so it cannot fire as either shape on its release.
        // The first update only learns the mode: nothing was in flight to spend.
        _tapHold ??= tapHold;
        if (tapHold != _tapHold)
        {
            _tapHold = tapHold;
            _down = held;
            _holdFired = held;
            _downAt = held ? now : -1;
            _firstTapAt = -1;
            _pendingTap = false;
            return Fire.None;
        }

        if (held && !_down)
        {
            _down = true;
            rising = true;
            _downAt = now;
            _holdFired = false;
            if (!tapHold)
            {
                if (_firstTapAt >= 0 && now - _firstTapAt <= DoubleTapMs)
                {
                    _firstTapAt = -1;
                    _pendingTap = false;
                    return Fire.DoubleTap;
                }
                _firstTapAt = now;
                _pendingTap = true;
            }
            return Fire.None;
        }

        if (tapHold)
        {
            if (!held && _down)
            {
                _down = false;
                return _holdFired ? Fire.None : Fire.Tap;
            }
            if (held && !_holdFired && now - _downAt >= HoldMs)
            {
                _holdFired = true;
                return Fire.Hold;
            }
            return Fire.None;
        }

        if (!held) _down = false;
        // Wait for the combo to be let go before acting on a single tap. Firing mid-hold meant
        // that holding the buttons down past the window parked the launcher under your thumbs,
        // and it also stole the press that was meant to be the second tap.
        if (_pendingTap && !held && _firstTapAt >= 0 && now - _firstTapAt > DoubleTapMs)
        {
            _pendingTap = false;
            _firstTapAt = -1;
            return Fire.Tap;
        }
        return Fire.None;
    }

    /// <summary>Forget the gesture in progress: the pad stopped being read (a recording, say).</summary>
    public void Reset()
    {
        _down = false;
        _downAt = -1;
        _holdFired = false;
        _firstTapAt = -1;
        _pendingTap = false;
    }
}

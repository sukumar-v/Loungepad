namespace Loungepad.Services;

/// <summary>
/// One button or combo as a gesture, one <see cref="Update"/> per poll. Up to three jobs can live
/// on the same chord, one per way of pressing it -- the Power Wheel and Loungepad are separate
/// settings, and by default both are on Guide, a tap for the wheel and a hold for Loungepad:
///
///  • Tap. Fired on the release, so it costs no more than the tap itself and never fires on the
///    way into a hold; a press held past <see cref="HoldMs"/> is never a tap. With a double tap also wanted, it waits out the double-tap window first,
///    which is the lag double tap is known for.
///  • Hold. Fired while still held, past <see cref="HoldMs"/>, so the hold answers at once; the
///    release after it does nothing.
///  • Double tap. Fired on the second press, if it comes within <see cref="DoubleTapMs"/> of the
///    first; that press is spent and neither holds nor taps.
///
/// Kept apart from the pad loop so the harness can replay presses against it with a fake clock.
/// </summary>
internal sealed class ComboGesture
{
    public enum Fire { None, Tap, DoubleTap, Hold }

    [Flags]
    public enum Want { None = 0, Tap = 1, Hold = 2, DoubleTap = 4 }

    public static Want WantOf(string? gesture) => gesture?.ToLowerInvariant() switch
    {
        "hold" => Want.Hold,
        "doubletap" => Want.DoubleTap,
        _ => Want.Tap,
    };

    public static Fire FireOf(string? gesture) => WantOf(gesture) switch
    {
        Want.Hold => Fire.Hold,
        Want.DoubleTap => Fire.DoubleTap,
        _ => Fire.Tap,
    };

    // Second tap within this window is a double tap. Measured from the first press, and the combo
    // can be two buttons, so the budget has to cover holding the first tap, releasing BOTH buttons,
    // and pressing again. 320ms did not: most double taps missed.
    public const int DoubleTapMs = 550;
    /// <summary>
    /// How long a hold is. Long enough that a relaxed tap never reaches it, short enough to feel
    /// like an answer. Well under the ~6 s it takes an Xbox pad to switch itself off, which is the
    /// other thing a held Xbox button does.
    /// </summary>
    public const int HoldMs = 500;

    /// <summary>What was wanted at the last update; null until the first, which is not a change.</summary>
    private Want? _want;
    private bool _down;
    private long _downAt = -1;
    /// <summary>This press has done its job (a hold or a double tap) and its release is nothing.</summary>
    private bool _spent;
    private long _firstTapAt = -1;
    private bool _pendingTap;

    /// <summary>What the combo does this poll. <paramref name="rising"/> is true on the poll it
    /// became held -- the pad loop swallows a keyboard chord inside the combo on that edge.</summary>
    public Fire Update(bool held, long now, Want want, out bool rising)
    {
        rising = false;

        // What is wanted changed under a gesture (a settings save). Start clean, and if the combo is
        // down right now treat that press as spent, so it cannot fire as anything on its release.
        // The first update only learns it: nothing was in flight to spend.
        _want ??= want;
        if (want != _want)
        {
            _want = want;
            _down = held;
            _spent = held;
            _downAt = held ? now : -1;
            _firstTapAt = -1;
            _pendingTap = false;
            return Fire.None;
        }
        if (want == Want.None) { _down = held; return Fire.None; }

        bool doubles = (want & Want.DoubleTap) != 0;

        if (held && !_down)
        {
            _down = true;
            rising = true;
            _downAt = now;
            _spent = false;
            if (doubles && _pendingTap && now - _firstTapAt <= DoubleTapMs)
            {
                _pendingTap = false;
                _firstTapAt = -1;
                _spent = true;
                return Fire.DoubleTap;
            }
            return Fire.None;
        }

        if (!held && _down)
        {
            _down = false;
            // A press held past the hold time is not a tap, whether or not a hold is wanted: with
            // the Loungepad button unset, holding Guide out of habit must not open the wheel on the
            // way back up.
            if (_spent || now - _downAt >= HoldMs) return Fire.None;
            // With a double tap on this chord a single one has to wait and see; without, it is
            // a tap now.
            if (doubles)
            {
                _pendingTap = true;
                _firstTapAt = _downAt;
                return Fire.None;
            }
            return (want & Want.Tap) != 0 ? Fire.Tap : Fire.None;
        }

        if (held && !_spent && (want & Want.Hold) != 0 && now - _downAt >= HoldMs)
        {
            _spent = true;
            _pendingTap = false;
            return Fire.Hold;
        }

        // Wait for the combo to be let go before acting on a single tap. Firing mid-hold meant
        // that holding the buttons down past the window parked the launcher under your thumbs,
        // and it also stole the press that was meant to be the second tap.
        if (_pendingTap && !held && now - _firstTapAt > DoubleTapMs)
        {
            _pendingTap = false;
            _firstTapAt = -1;
            return (want & Want.Tap) != 0 ? Fire.Tap : Fire.None;
        }
        return Fire.None;
    }

    /// <summary>Forget the gesture in progress: the pad stopped being read (a recording, say).</summary>
    public void Reset()
    {
        _down = false;
        _downAt = -1;
        _spent = false;
        _firstTapAt = -1;
        _pendingTap = false;
    }
}

/// <summary>
/// The Power Wheel and Loungepad buttons together: a <see cref="ComboGesture"/> per distinct chord,
/// each wanting the gestures of every setting on it. When both settings ask for the same chord AND
/// the same gesture -- which Settings warns about -- the wheel takes it, since the wheel can always
/// reach Loungepad and not the other way round.
/// </summary>
internal sealed class ComboSet
{
    public enum Job { Wheel, Launcher }

    private readonly Dictionary<uint, ComboGesture> _byChord = new();
    private readonly List<Job> _fired = new();

    public IReadOnlyList<Job> Update(uint held, long now, Models.AppSettings s, out bool rising)
    {
        rising = false;
        _fired.Clear();
        uint wheel = GamepadService.ChordMask(s.WheelCombo), launcher = GamepadService.ChordMask(s.LauncherCombo);
        foreach (var chord in _byChord.Keys.ToList())
            if (chord != wheel && chord != launcher) _byChord.Remove(chord);

        foreach (var chord in new[] { wheel, launcher }.Where(c => c != 0).Distinct())
        {
            var want = (chord == wheel ? ComboGesture.WantOf(s.WheelGesture) : 0)
                     | (chord == launcher ? ComboGesture.WantOf(s.LauncherGesture) : 0);
            if (!_byChord.TryGetValue(chord, out var g)) _byChord[chord] = g = new ComboGesture();
            var fire = g.Update((held & chord) == chord, now, want, out bool r);
            rising |= r;
            if (fire == ComboGesture.Fire.None) continue;
            if (chord == wheel && fire == ComboGesture.FireOf(s.WheelGesture)) _fired.Add(Job.Wheel);
            else if (chord == launcher && fire == ComboGesture.FireOf(s.LauncherGesture)) _fired.Add(Job.Launcher);
        }
        return _fired;
    }

    public void Reset()
    {
        foreach (var g in _byChord.Values) g.Reset();
    }
}

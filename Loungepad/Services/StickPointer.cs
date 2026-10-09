namespace Loungepad.Services;

/// <summary>
/// The stick as a pointer, in one place. The launcher (GamepadService, with no input service)
/// and the input agent (SecureMapper, on the desktop, over a UAC prompt and on the sign-in
/// screen) move the pointer with exactly this, so a push feels the same everywhere. It used to
/// be two formulas: the agent curved each axis on its own, which made a diagonal push up to
/// 41% faster than the launcher's magnitude-based curve -- "fast on the admin prompts".
///
/// Shared by source: this file is compiled into both Loungepad and Loungepad.Input, so it must
/// stay plain C# with no WPF, no logging and no settings types.
/// </summary>
internal static class StickPointer
{
    public const double MaxSpeedPxPerSec = 1400;
    public const double MaxScrollNotchesPerSec = 18;
    /// <summary>Analog triggers count as "pressed" past this.</summary>
    public const byte TriggerThreshold = 40;

    /// <summary>
    /// Pointer velocity in pixels per second for a left-stick reading: the deflection past the
    /// deadzone, rescaled so movement starts at zero right past it, through the acceleration
    /// curve, along the stick's own direction. Screen Y grows downward, so a push up is negative.
    /// </summary>
    public static (double X, double Y) Velocity(short lx, short ly, double deadzone, double sensitivity, double accelExponent, double boost)
    {
        double nx = lx / 32767.0, ny = ly / 32767.0;
        double mag = Math.Sqrt(nx * nx + ny * ny);
        if (mag <= deadzone) return (0, 0);
        double t = Math.Min((mag - deadzone) / (1 - deadzone), 1.0);
        double speed = MaxSpeedPxPerSec * sensitivity * Math.Pow(t, accelExponent) * boost;
        return (nx / mag * speed, -ny / mag * speed);
    }

    /// <summary>How far a stick has to travel from where it last registered before that counts as
    /// the pad being used: 5% of its range. A DualSense streams a report every 4 ms and its sticks
    /// rest a few percent off centre, and an Xbox pad's wobble at rest must not take the mapping
    /// from the pad someone is actually holding.</summary>
    public const int StickNoise = 1600;

    /// <summary>Has this pad been touched since it last registered? Buttons count at once; the
    /// analog parts past the noise. The same test as the launcher's pad selection.</summary>
    public static bool Moved(in Interop.NativeMethods.XINPUT_GAMEPAD a, in Interop.NativeMethods.XINPUT_GAMEPAD b) =>
        a.wButtons != b.wButtons
        || Math.Abs(a.bLeftTrigger - b.bLeftTrigger) > 24
        || Math.Abs(a.bRightTrigger - b.bRightTrigger) > 24
        || Math.Abs(a.sThumbLX - b.sThumbLX) > StickNoise
        || Math.Abs(a.sThumbLY - b.sThumbLY) > StickNoise
        || Math.Abs(a.sThumbRX - b.sThumbRX) > StickNoise
        || Math.Abs(a.sThumbRY - b.sThumbRY) > StickNoise;

    /// <summary>One axis of stick scrolling as a speed in wheel notches per second, up positive.</summary>
    public static double ScrollRate(double axis, double deadzone, double boost)
    {
        double mag = Math.Abs(axis);
        if (mag <= deadzone) return 0;
        double t = Math.Min((mag - deadzone) / (1 - deadzone), 1.0);
        return Math.Sign(axis) * MaxScrollNotchesPerSec * Math.Pow(t, 1.5) * boost;
    }

    /// <summary>
    /// Accumulates notches per elapsed time and emits whole ones. Returns the remainder to carry
    /// into the next tick; zero inside the deadzone, so a release never emits a last notch.
    /// </summary>
    public static double Scroll(double axis, double deadzone, double dt, double boost, double accum, Action<int> emit)
    {
        double rate = ScrollRate(axis, deadzone, boost);
        if (rate == 0) return 0;
        accum += rate * dt;
        int notches = (int)accum;
        if (notches != 0)
        {
            accum -= notches;
            emit(notches);
        }
        return accum;
    }

    /// <summary>
    /// Whole pixels per tick, carrying the fraction between ticks so a slow push still adds up.
    /// Reset in the deadzone, so a release never dribbles out a last pixel.
    /// </summary>
    public sealed class Mover
    {
        private double _fracX, _fracY;

        public (int Dx, int Dy) Step(short lx, short ly, double dt, double deadzone, double sensitivity, double accelExponent, double boost)
        {
            var (vx, vy) = Velocity(lx, ly, deadzone, sensitivity, accelExponent, boost);
            if (vx == 0 && vy == 0) { Reset(); return (0, 0); }
            _fracX += vx * dt;
            _fracY += vy * dt;
            int dx = (int)_fracX, dy = (int)_fracY;
            _fracX -= dx; _fracY -= dy;
            return (dx, dy);
        }

        public void Reset() { _fracX = 0; _fracY = 0; }
    }
}

using System.Runtime.InteropServices;

namespace Loungepad.Input;

internal static class InputDesktopAccess
{
    // User-space input policy must not fire launcher actions while a secure desktop owns focus.
    public static bool Available()
    {
        var desktop = OpenInputDesktop(0, false, 1);
        if (desktop == IntPtr.Zero) return false;
        CloseDesktop(desktop); return true;
    }
    [DllImport("user32.dll")] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
}

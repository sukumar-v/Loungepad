using System.Runtime.InteropServices;
using System.Text;

namespace Loungepad.Interop;

internal static class NativeMethods
{
    // ---- Display enumeration / primary switching ----

    public const int ENUM_CURRENT_SETTINGS = -1;
    public const uint DM_POSITION = 0x00000020;

    public const uint CDS_UPDATEREGISTRY = 0x00000001;
    public const uint CDS_NORESET = 0x10000000;
    public const uint CDS_SET_PRIMARY = 0x00000010;

    public const uint DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x00000001;
    public const uint DISPLAY_DEVICE_PRIMARY_DEVICE = 0x00000004;
    public const uint DISPLAY_DEVICE_MIRRORING_DRIVER = 0x00000008;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAY_DEVICE
    {
        public uint cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool EnumDisplaySettings(string lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int ChangeDisplaySettingsEx(string lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int ChangeDisplaySettingsEx(string? lpszDeviceName, IntPtr lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);

    // ---- Window management ----

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new(-2);
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    /// <summary>Lets another process take the foreground next time it asks. Only honoured while
    /// the calling process is itself the foreground process -- so it has to be called before the
    /// launcher parks, not after.</summary>
    [DllImport("user32.dll")]
    public static extern bool AllowSetForegroundWindow(uint dwProcessId);

    [DllImport("user32.dll")]
    public static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    public const uint WM_SYSCOMMAND = 0x0112;
    public const int SC_CLOSE = 0xF060;
    public const int SC_MONITORPOWER = 0xF170;
    public const int MONITOR_OFF = 2, MONITOR_ON = -1;
    public static readonly IntPtr HWND_BROADCAST = new(0xFFFF);

    [DllImport("user32.dll")]
    public static extern IntPtr PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>Synchronous, so only ever to a window of this process (see WindowService.MonitorPower).</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public const uint SMTO_ABORTIFHUNG = 0x0002;

    /// <summary>Broadcast form of SendMessage. The timeout matters: a plain broadcast blocks on
    /// any hung top-level window, which would freeze the UI thread.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeoutMs, out IntPtr result);

    // ---- Process image path (more reliable than Process.MainModule cross-arch) ----

    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll")]
    public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

    // ---- Cursor / mouse input ----

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);

    // ---- A monitor's scaling ----

    public const uint MONITOR_DEFAULTTONEAREST = 2;
    public const int MDT_EFFECTIVE_DPI = 0;

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    /// <summary>The DPI Windows scales that monitor to (96 = 100%). The process is per-monitor
    /// aware (app.manifest), so this is the monitor's own number, not the system's.</summary>
    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    // ---- System cursor replacement (optional system-wide pointer hiding) ----

    public const uint OCR_NORMAL = 32512;
    public const uint OCR_IBEAM = 32513;
    public const uint OCR_WAIT = 32514;
    public const uint OCR_CROSS = 32515;
    public const uint OCR_UP = 32516;
    public const uint OCR_SIZENWSE = 32642;
    public const uint OCR_SIZENESW = 32643;
    public const uint OCR_SIZEWE = 32644;
    public const uint OCR_SIZENS = 32645;
    public const uint OCR_SIZEALL = 32646;
    public const uint OCR_NO = 32648;
    public const uint OCR_HAND = 32649;
    public const uint OCR_APPSTARTING = 32650;

    public static readonly uint[] SystemCursorIds =
    {
        OCR_NORMAL, OCR_IBEAM, OCR_CROSS, OCR_UP, OCR_SIZENWSE, OCR_SIZENESW,
        OCR_SIZEWE, OCR_SIZENS, OCR_SIZEALL, OCR_NO, OCR_HAND, OCR_APPSTARTING
    };

    public const uint SPI_SETCURSORS = 0x0057;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr CreateCursor(IntPtr hInst, int xHotSpot, int yHotSpot,
        int nWidth, int nHeight, byte[] pvANDPlane, byte[] pvXORPlane);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetSystemCursor(IntPtr hcur, uint id);

    [DllImport("user32.dll")]
    public static extern bool DestroyCursor(IntPtr hCursor);

    public const uint INPUT_MOUSE = 0;
    public const uint MOUSEEVENTF_MOVE = 0x0001;
    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;
    public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    public const uint MOUSEEVENTF_WHEEL = 0x0800;
    public const uint MOUSEEVENTF_HWHEEL = 0x1000;
    public const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    public const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int index);

    // ---- GDI screen capture (used for the still behind an overlay menu) ----

    public const int SRCCOPY = 0x00CC0020;
    /// <summary>Include layered windows in the blit; without it they come out as holes.</summary>
    public const int CAPTUREBLT = 0x40000000;

    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hDC);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleBitmap(IntPtr hDC, int w, int h);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hDC, IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hDC);
    [DllImport("gdi32.dll")]
    public static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);

    public const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77;
    public const int SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    /// <summary>
    /// The real INPUT union. This used to be collapsed to MOUSEINPUT alone, which happened to be
    /// safe only because that is the larger member: a keyboard event written through it would have
    /// landed wVk and wScan in the right bytes by luck rather than by declaration.
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    public struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    /// <summary>
    /// Put the pointer at an absolute screen position as *injected mouse input*, not with
    /// SetCursorPos. SetCursorPos only relocates the cursor; applications are not reliably told
    /// it moved, and WebView2 in particular fired no DOM mousemove for it — so the stick pushed
    /// a pointer around the launcher that never highlighted anything it passed over. SendInput
    /// goes in at the driver level, so the move is indistinguishable from a real mouse.
    /// Absolute coordinates (rather than a relative delta) keep the pointer exactly where the
    /// caller asked, unaffected by the user's pointer-speed and acceleration settings.
    /// </summary>
    public static void MoveCursorTo(int x, int y)
    {
        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN), vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN), vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        if (vw <= 1 || vh <= 1) { SetCursorPos(x, y); return; }

        // The absolute range is 0..65535 across the whole virtual desktop, and the mapping
        // truncates, so aim at the middle of the target pixel to avoid drifting a pixel short.
        int nx = (int)(((x - vx) * 65535.0 + 32767.0) / (vw - 1));
        int ny = (int)(((y - vy) * 65535.0 + 32767.0) / (vh - 1));

        var input = new[]
        {
            new INPUT
            {
                type = INPUT_MOUSE,
                u = new INPUTUNION
                {
                    mi = new MOUSEINPUT
                    {
                        dx = Math.Clamp(nx, 0, 65535),
                        dy = Math.Clamp(ny, 0, 65535),
                        dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
                    },
                },
            },
        };
        SendInput(1, input, Marshal.SizeOf<INPUT>());
    }

    // ---- keyboard injection ----

    public const uint INPUT_KEYBOARD = 1;
    public const uint KEYEVENTF_KEYUP = 0x0002;

    public const ushort VK_F12 = 0x7B;
    public const ushort SCAN_F12 = 0x58;

    /// <summary>
    /// Tap a key as injected keyboard input. Both the virtual key and the scan code go in:
    /// overlays like Steam's hook the virtual key, while anything reading raw input or
    /// DirectInput only ever sees the scan code, and a screenshot key has to reach whichever
    /// one is listening.
    /// </summary>
    public static void SendKeyTap(ushort vk, ushort scan)
    {
        var inputs = new[]
        {
            new INPUT { type = INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk, wScan = scan } } },
            new INPUT { type = INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = KEYEVENTF_KEYUP } } },
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    // ---- non-activating overlay window ----

    /// <summary>
    /// A window with this style is never given the foreground, so an on-screen keyboard can sit
    /// over a game while the game keeps focus and its text caret. Without it the keyboard would
    /// take the foreground the moment it appeared and every keystroke would go to the keyboard
    /// itself.
    /// </summary>
    public const long WS_EX_NOACTIVATE = 0x08000000;

    public const uint WM_MOUSEACTIVATE = 0x0021;
    public const int MA_NOACTIVATE = 3;

    public const int WM_CAPTURECHANGED = 0x0215;

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    public const uint KEYEVENTF_UNICODE = 0x0004;

    public const ushort VK_BACK = 0x08, VK_TAB = 0x09, VK_RETURN = 0x0D, VK_ESCAPE = 0x1B;
    public const ushort VK_LEFT = 0x25, VK_RIGHT = 0x27, VK_HOME = 0x24, VK_END = 0x23, VK_DELETE = 0x2E;

    /// <summary>
    /// Type one character as injected keyboard input. KEYEVENTF_UNICODE carries the character
    /// itself rather than a key position, so the result does not depend on the user's keyboard
    /// layout -- pressing the on-screen "q" types q on AZERTY too. Surrogate pairs go through as
    /// two events, which is what Windows expects.
    /// </summary>
    public static void SendChar(char ch)
    {
        var inputs = new[]
        {
            new INPUT { type = INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE } } },
            new INPUT { type = INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } } },
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    /// <summary>Tap a virtual key by code, letting Windows supply the scan code.</summary>
    public static void SendVirtualKey(ushort vk, bool extended = false)
    {
        uint extra = extended ? KEYEVENTF_EXTENDEDKEY : 0;
        var inputs = new[]
        {
            new INPUT { type = INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk, dwFlags = extra } } },
            new INPUT { type = INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk, dwFlags = extra | KEYEVENTF_KEYUP } } },
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    public const ushort VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_PAUSE = 0x13, VK_SPACE = 0x20;
    public const ushort VK_PRIOR = 0x21, VK_NEXT = 0x22, VK_UP = 0x26, VK_DOWN = 0x28;
    public const ushort VK_SNAPSHOT = 0x2C, VK_INSERT = 0x2D, VK_LWIN = 0x5B;
    public const ushort VK_NUMPAD0 = 0x60, VK_MULTIPLY = 0x6A, VK_ADD = 0x6B, VK_SUBTRACT = 0x6D;
    public const ushort VK_DECIMAL = 0x6E, VK_DIVIDE = 0x6F, VK_F1 = 0x70, VK_NUMLOCK = 0x90, VK_SCROLL = 0x91;
    // The keys an action's shortcut can name beyond the keyboard's own (see ShortcutKeys): the
    // application key, and the volume, media and browser keys, which are all extended keys.
    public const ushort VK_CAPITAL = 0x14, VK_APPS = 0x5D;
    public const ushort VK_BROWSER_BACK = 0xA6, VK_BROWSER_FORWARD = 0xA7, VK_BROWSER_REFRESH = 0xA8;
    public const ushort VK_BROWSER_SEARCH = 0xAA, VK_BROWSER_FAVORITES = 0xAB, VK_BROWSER_HOME = 0xAC;
    public const ushort VK_VOLUME_MUTE = 0xAD, VK_VOLUME_DOWN = 0xAE, VK_VOLUME_UP = 0xAF;
    public const ushort VK_MEDIA_NEXT_TRACK = 0xB0, VK_MEDIA_PREV_TRACK = 0xB1, VK_MEDIA_STOP = 0xB2, VK_MEDIA_PLAY_PAUSE = 0xB3;

    [DllImport("user32.dll")]
    public static extern uint MapVirtualKeyW(uint uCode, uint uMapType);
    private const uint MAPVK_VK_TO_VSC = 0;

    /// <summary>The key and shift state that type a character on the current layout: the low byte
    /// is the virtual key, the high byte 1 for Shift, 2 for Ctrl, 4 for Alt. -1 when no key does.</summary>
    [DllImport("user32.dll")]
    public static extern short VkKeyScanW(char ch);

    /// <summary>
    /// Tap a key with modifiers held around it, as one batch so nothing can land in between.
    ///
    /// Unlike SendVirtualKey this carries the scan code as well. A game reading raw input or
    /// DirectInput sees only the scan code, and those are exactly the programs that bind F5 or a
    /// number-pad key. Pause and Num Lock share 0x45 (Pause is really E1 1D 45), so Pause goes by
    /// virtual key alone; Print Screen's mapping differs between layouts and it goes the same way.
    /// </summary>
    public static void SendKeyCombo(ushort vk, bool extended, IReadOnlyList<ushort> modifiers)
    {
        var inputs = new List<INPUT>(modifiers.Count * 2 + 2);
        foreach (var m in modifiers) inputs.Add(KeyInput(m, m == VK_LWIN, up: false));
        inputs.Add(KeyInput(vk, extended, up: false));
        inputs.Add(KeyInput(vk, extended, up: true));
        for (int i = modifiers.Count - 1; i >= 0; i--) inputs.Add(KeyInput(modifiers[i], modifiers[i] == VK_LWIN, up: true));
        SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
    }

    private static INPUT KeyInput(ushort vk, bool extended, bool up)
    {
        ushort scan = vk is VK_PAUSE or VK_SNAPSHOT ? (ushort)0 : (ushort)MapVirtualKeyW(vk, MAPVK_VK_TO_VSC);
        uint flags = (extended ? KEYEVENTF_EXTENDEDKEY : 0) | (up ? KEYEVENTF_KEYUP : 0);
        return new INPUT { type = INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags } } };
    }

    /// <summary>Type a run of characters as one batch, each as KEYEVENTF_UNICODE (see SendChar).</summary>
    public static void SendText(string text)
    {
        if (text.Length == 0) return;
        var inputs = new INPUT[text.Length * 2];
        for (int i = 0; i < text.Length; i++)
        {
            inputs[i * 2] = new INPUT { type = INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT { wScan = text[i], dwFlags = KEYEVENTF_UNICODE } } };
            inputs[i * 2 + 1] = new INPUT { type = INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT { wScan = text[i], dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } } };
        }
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GUITHREADINFO
    {
        public int cbSize;
        public uint flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public RECT rcCaret;
    }

    /// <summary>A thread's focus and caret. Works across processes, needs no hook, and reads nothing
    /// but window handles and a rectangle.</summary>
    [DllImport("user32.dll")]
    public static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

    // ---- XInput ----

    public const ushort XINPUT_GAMEPAD_DPAD_UP = 0x0001;
    public const ushort XINPUT_GAMEPAD_DPAD_DOWN = 0x0002;
    public const ushort XINPUT_GAMEPAD_DPAD_LEFT = 0x0004;
    public const ushort XINPUT_GAMEPAD_DPAD_RIGHT = 0x0008;
    public const ushort XINPUT_GAMEPAD_START = 0x0010;
    public const ushort XINPUT_GAMEPAD_BACK = 0x0020;
    public const ushort XINPUT_GAMEPAD_LEFT_THUMB = 0x0040;
    public const ushort XINPUT_GAMEPAD_RIGHT_THUMB = 0x0080;
    public const ushort XINPUT_GAMEPAD_LEFT_SHOULDER = 0x0100;
    public const ushort XINPUT_GAMEPAD_RIGHT_SHOULDER = 0x0200;
    public const ushort XINPUT_GAMEPAD_A = 0x1000;
    public const ushort XINPUT_GAMEPAD_B = 0x2000;
    public const ushort XINPUT_GAMEPAD_X = 0x4000;
    public const ushort XINPUT_GAMEPAD_Y = 0x8000;

    [StructLayout(LayoutKind.Sequential)]
    public struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern int XInputGetState14(int dwUserIndex, out XINPUT_STATE pState);

    [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")]
    private static extern int XInputGetState910(int dwUserIndex, out XINPUT_STATE pState);

    // Battery (xinput1_4 only)
    public const byte BATTERY_DEVTYPE_GAMEPAD = 0;
    public const byte BATTERY_TYPE_DISCONNECTED = 0x00;
    public const byte BATTERY_TYPE_WIRED = 0x01;
    public const byte BATTERY_TYPE_ALKALINE = 0x02;
    public const byte BATTERY_TYPE_NIMH = 0x03;

    [StructLayout(LayoutKind.Sequential)]
    public struct XINPUT_BATTERY_INFORMATION
    {
        public byte BatteryType;
        public byte BatteryLevel; // 0 empty .. 3 full
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetBatteryInformation")]
    private static extern int XInputGetBatteryInformation14(int dwUserIndex, byte devType, out XINPUT_BATTERY_INFORMATION pBatteryInformation);

    public static bool TryGetBattery(int userIndex, out XINPUT_BATTERY_INFORMATION info)
    {
        info = default;
        if (_xinput14Missing) return false;
        try { return XInputGetBatteryInformation14(userIndex, BATTERY_DEVTYPE_GAMEPAD, out info) == 0; }
        catch (DllNotFoundException) { _xinput14Missing = true; return false; }
        catch (EntryPointNotFoundException) { return false; }
    }


    // ---- Bluetooth battery (the number Settings shows) ----
    //
    // A pad on Bluetooth reports its real charge to the BLE Battery Service, and Windows caches
    // that on the Bluetooth device node. Neither XInput nor WinRT will hand it over: XInput calls
    // an Xbox pad on Bluetooth "disconnected" with level 0, and WinRT's battery report just dresses
    // that same coarse level up as milliwatt-hours -- 100 of 1000, i.e. 10%, for a pad Settings was
    // showing at 97%.

    private const uint CR_SUCCESS = 0;
    private const uint CM_GETIDLIST_FILTER_ENUMERATOR = 0x00000001;
    private const uint CM_GETIDLIST_FILTER_PRESENT = 0x00000100;
    private const uint DEVPROP_TYPE_BYTE = 0x00000003;
    private const uint DEVPROP_TYPE_GUID = 0x0000000D;

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY { public Guid fmtid; public uint pid; }

    /// <summary>Undocumented but stable: the 0-100 battery level shown on the Bluetooth settings page.</summary>
    private static readonly DEVPROPKEY DEVPKEY_Bluetooth_Battery =
        new() { fmtid = new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"), pid = 2 };

    /// <summary>Identifies the physical device, so the pad's HID node and its Bluetooth node can be tied together.</summary>
    private static readonly DEVPROPKEY DEVPKEY_Device_ContainerId =
        new() { fmtid = new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), pid = 2 };

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_ID_List_SizeW(out uint pulLen, string? pszFilter, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_ID_ListW(string? pszFilter, char[] buffer, uint bufferLen, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_DevNode_PropertyW(uint dnDevInst, in DEVPROPKEY key, out uint propertyType,
        byte[]? propertyBuffer, ref uint propertyBufferSize, uint ulFlags);

    /// <summary>Instance ids of every device currently present under one enumerator ("HID", "BTHLE", ...).</summary>
    private static IEnumerable<string> PresentDevices(string enumerator)
    {
        const uint flags = CM_GETIDLIST_FILTER_ENUMERATOR | CM_GETIDLIST_FILTER_PRESENT;
        if (CM_Get_Device_ID_List_SizeW(out uint len, enumerator, flags) != CR_SUCCESS || len == 0)
            return Array.Empty<string>();

        var buffer = new char[len];
        if (CM_Get_Device_ID_ListW(enumerator, buffer, len, flags) != CR_SUCCESS)
            return Array.Empty<string>();

        return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>One device-node property, or null when the node has no such property of that type.</summary>
    private static byte[]? DevNodeProperty(string instanceId, in DEVPROPKEY key, uint expectedType)
    {
        if (CM_Locate_DevNodeW(out uint devInst, instanceId, 0) != CR_SUCCESS) return null;

        uint size = 0;
        CM_Get_DevNode_PropertyW(devInst, key, out uint type, null, ref size, 0);   // asks for the size
        if (size == 0 || type != expectedType) return null;

        var buffer = new byte[size];
        return CM_Get_DevNode_PropertyW(devInst, key, out _, buffer, ref size, 0) == CR_SUCCESS ? buffer : null;
    }

    /// <summary>
    /// Charge of the attached gamepad as a real percentage, or null when it is not on Bluetooth (or
    /// is not reporting). Gamepads are picked out by the "IG_" in their HID instance id, which is
    /// how XInput itself marks the devices it drives.
    /// </summary>
    /// <param name="hidInstanceId">A specific pad's device node. When given, only that pad's radio
    /// is consulted, so an Xbox pad on the same PC cannot answer for a DualSense.</param>
    public static bool TryGetBluetoothBatteryPercent(out int percent, string? hidInstanceId = null)
    {
        percent = 0;
        try
        {
            var pads = new List<Guid>();
            if (hidInstanceId is not null)
            {
                if (DevNodeProperty(hidInstanceId, DEVPKEY_Device_ContainerId, DEVPROP_TYPE_GUID) is { Length: 16 } hg)
                    pads.Add(new Guid(hg));
            }
            else
            {
                foreach (var id in PresentDevices("HID"))
                    if (id.Contains("IG_", StringComparison.OrdinalIgnoreCase)
                        && DevNodeProperty(id, DEVPKEY_Device_ContainerId, DEVPROP_TYPE_GUID) is { Length: 16 } g)
                        pads.Add(new Guid(g));
            }

            if (pads.Count == 0) return false;

            // BTHLE covers Bluetooth LE (an Xbox pad), BTHENUM classic Bluetooth (a DualSense).
            foreach (var enumerator in new[] { "BTHLE", "BTHENUM" })
                foreach (var id in PresentDevices(enumerator))
                {
                    if (DevNodeProperty(id, DEVPKEY_Bluetooth_Battery, DEVPROP_TYPE_BYTE) is not { Length: 1 } level)
                        continue;
                    if (DevNodeProperty(id, DEVPKEY_Device_ContainerId, DEVPROP_TYPE_GUID) is not { Length: 16 } cid)
                        continue;
                    // Headsets, keyboards and mice report a battery too; only the node sharing a
                    // physical device with a gamepad is ours.
                    if (!pads.Contains(new Guid(cid))) continue;

                    percent = Math.Min((int)level[0], 100);
                    return true;
                }
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        return false;
    }


    // ---- Window control (Power Wheel) ----

    public const int SW_MINIMIZE = 6;
    public const int SW_RESTORE = 9;
    public const int SW_SHOW = 5;

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetShellWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);


    // ---- window list filtering (the Alt+Tab rules) ----

    /// <summary>
    /// A window is "cloaked" when it exists but the shell is deliberately not showing it. UWP keeps
    /// a cloaked window per suspended app under ApplicationFrameHost, and IsWindowVisible still
    /// answers true for those -- which is why they turned up in the switcher as ghosts.
    /// </summary>
    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    public const int DWMWA_CLOAKED = 14;

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    public const uint GA_ROOTOWNER = 3;

    public const int GWL_STYLE = -16;
    public const long WS_VISIBLE = 0x10000000L;
    public const long WS_CHILD = 0x40000000L;
    /// <summary>Set on a window the owner has asked not to appear in the switcher.</summary>
    public const long WS_EX_NOREDIRECTIONBITMAP = 0x00200000L;
    public const long WS_EX_APPWINDOW = 0x00040000L;

    // ---- window thumbnails ----

    /// <summary>
    /// Asks a window to paint itself into a DC. PW_RENDERFULLCONTENT is what makes this work for
    /// DirectComposition and UWP windows, which render nothing under the older flag.
    /// </summary>
    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint flags);
    public const uint PW_RENDERFULLCONTENT = 0x00000002;

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindowDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hwnd);

    // ---- window icons ----
    // The fallback when a window cannot be photographed. A minimized window has no surface to
    // paint, so PrintWindow gives back an empty bitmap; its icon is always available.

    public const uint WM_GETICON = 0x007F;
    public const int ICON_SMALL = 0, ICON_BIG = 1, ICON_SMALL2 = 2;
    public const int GCLP_HICON = -14, GCLP_HICONSM = -34;

    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
    public static extern IntPtr GetClassLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>
    /// The icon in an exe, at a size of our choosing: the frame nearest cx by cy is picked from
    /// the icon group, so a 96px request gets the 96 or 128 frame most programs ship rather than
    /// the 32px one ExtractAssociatedIcon stops at. Returns how many icons were extracted.
    /// </summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint PrivateExtractIcons(string lpszFile, int nIconIndex, int cxIcon, int cyIcon,
        IntPtr[] phicon, uint[] piconid, uint nIcons, uint flags);

    /// <summary>The children of a window. A Store app's real window is a child of the frame
    /// host's, and the frame host is the process that owns the top-level one.</summary>
    [DllImport("user32.dll")]
    public static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);


    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GetProcAddress(IntPtr hModule, IntPtr ordinal);

    // ---- Guide button ----
    // XInput's public API deliberately hides the Guide/PS button. Ordinal 100 of the XInput DLLs
    // is the long-standing undocumented XInputGetStateEx, which reports it as bit 0x0400. If the
    // export is missing we fall back to the documented call and the Guide bit simply never sets.
    public const ushort XINPUT_GAMEPAD_GUIDE = 0x0400;
    /// <summary>
    /// The pad's own screenshot button, in the one bit XInput leaves unused: Capture on a Switch
    /// Pro. Set only by HidGamepadReader -- XInput never reports the Xbox Share button, which
    /// Windows keeps to itself -- and never a binding, a combo or a UI button.
    /// </summary>
    public const ushort XINPUT_GAMEPAD_SHARE = 0x0800;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int XInputGetStateExFn(int dwUserIndex, out XINPUT_STATE pState);

    private static XInputGetStateExFn? _getStateEx;
    private static bool _exProbed;

    public static bool GuideSupported
    {
        get { ProbeStateEx(); return _getStateEx is not null; }
    }

    private static void ProbeStateEx()
    {
        if (_exProbed) return;
        _exProbed = true;
        foreach (var dll in new[] { "xinput1_4.dll", "xinput1_3.dll" })
        {
            var h = LoadLibrary(dll);
            if (h == IntPtr.Zero) continue;
            var p = GetProcAddress(h, new IntPtr(100));
            if (p == IntPtr.Zero) continue;
            _getStateEx = Marshal.GetDelegateForFunctionPointer<XInputGetStateExFn>(p);
            return;
        }
    }

    /// <summary>State including the Guide bit when the extended export exists.</summary>
    public static int XInputGetStateAny(int userIndex, out XINPUT_STATE state)
    {
        ProbeStateEx();
        if (_getStateEx is not null)
        {
            try { return _getStateEx(userIndex, out state); }
            catch { _getStateEx = null; }
        }
        return XInputGetState(userIndex, out state);
    }
    private static bool _xinput14Missing;

    public static int XInputGetState(int userIndex, out XINPUT_STATE state)
    {
        if (!_xinput14Missing)
        {
            try { return XInputGetState14(userIndex, out state); }
            catch (DllNotFoundException) { _xinput14Missing = true; }
        }
        return XInputGetState910(userIndex, out state);
    }

    // ---- Hardware readings for the activity log (HardwareMonitor) ----

    /// <summary>Idle, kernel and user time as FILETIME ticks; the system CPU load is the change in
    /// idle over the change in kernel plus user between two calls. What Task Manager reads.</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetSystemTimes(out long lpIdleTime, out long lpKernelTime, out long lpUserTime);

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    // PDH, the performance counter library: the one place Windows publishes GPU utilisation
    // without a vendor SDK (\GPU Engine(*)\Utilization Percentage, per process and engine, which
    // is what Task Manager's GPU column is read from). The counter array is walked by hand:
    // PDH_FMT_COUNTERVALUE_ITEM_W is a wide-string pointer followed by a 16-byte value union, 24
    // bytes an item on x64, with the status at +8 and the double at +16.
    public const uint PDH_FMT_DOUBLE = 0x00000200;
    public const uint PDH_MORE_DATA = 0x800007D2;
    public const int PDH_ITEM_SIZE = 24;

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    public static extern uint PdhOpenQueryW(string? szDataSource, IntPtr dwUserData, out IntPtr phQuery);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    public static extern uint PdhAddEnglishCounterW(IntPtr hQuery, string szFullCounterPath, IntPtr dwUserData, out IntPtr phCounter);

    [DllImport("pdh.dll")]
    public static extern uint PdhCollectQueryData(IntPtr hQuery);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    public static extern uint PdhGetFormattedCounterArrayW(IntPtr hCounter, uint dwFormat, ref uint lpdwBufferSize, out uint lpdwItemCount, IntPtr itemBuffer);

    [DllImport("pdh.dll")]
    public static extern uint PdhCloseQuery(IntPtr hQuery);

    // ---- Rest mode: idle time, sleep, and freezing a game (RestService, GameLaunchService, WakeInfo) ----

    [StructLayout(LayoutKind.Sequential)]
    public struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    /// <summary>The tick (GetTickCount, 32-bit) of the last keyboard or mouse input in this
    /// session, whoever it went to. Compared unsigned against GetTickCount so the 49-day wrap
    /// does not matter. A gamepad read through XInput or Raw Input is not input to Windows and
    /// never moves it; the pad service keeps its own stamp.</summary>
    [DllImport("user32.dll")]
    public static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("kernel32.dll")]
    public static extern uint GetTickCount();

    /// <summary>Put the machine to sleep (or hibernate). Hybrid sleep, where Windows has it on,
    /// is honoured. Returns only once the machine is back, so it is never called on the UI
    /// thread; a FALSE within a moment means the request was refused.</summary>
    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool SetSuspendState([MarshalAs(UnmanagedType.U1)] bool hibernate,
        [MarshalAs(UnmanagedType.U1)] bool forceCritical, [MarshalAs(UnmanagedType.U1)] bool disableWakeEvent);

    [DllImport("powrprof.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool IsPwrSuspendAllowed();

    /// <summary>CallNtPowerInformation levels used here: SYSTEM_POWER_CAPABILITIES (76 bytes; S3
    /// at byte 5, AoAc -- Modern Standby -- at byte 20) and the execution state (a ULONG of ES_*
    /// bits: what is holding the system or the display awake right now).</summary>
    public const int SystemPowerCapabilities = 4, SystemExecutionState = 16;
    public const uint ES_SYSTEM_REQUIRED = 0x1, ES_DISPLAY_REQUIRED = 0x2;

    [DllImport("powrprof.dll")]
    public static extern uint CallNtPowerInformation(int level, IntPtr inBuf, uint inLen, byte[] outBuf, uint outLen);

    // The active power scheme's "Require a password on wakeup" (CONSOLELOCK, under the no-subgroup
    // guid). Readable by anyone; writing it is an elevated powercfg (see WakeInfo).
    public static readonly Guid NO_SUBGROUP_GUID = new("fea3413e-7e05-4911-9a71-700331f1c294");
    public static readonly Guid GUID_LOCK_CONSOLE_ON_WAKE = new("0e796bdb-100d-47d6-a2d5-f7d2daa51f51");

    [DllImport("powrprof.dll")]
    public static extern uint PowerGetActiveScheme(IntPtr rootKey, out IntPtr activeScheme);

    [DllImport("powrprof.dll")]
    public static extern uint PowerReadACValueIndex(IntPtr rootKey, ref Guid scheme, ref Guid subGroup, ref Guid setting, out uint value);

    [DllImport("kernel32.dll")]
    public static extern IntPtr LocalFree(IntPtr hMem);

    /// <summary>Freeze and thaw every thread of a process at once: what PlayState does to pause a
    /// game, and what a debugger's "break" is. Undocumented but unchanged since XP. The handle
    /// needs PROCESS_SUSPEND_RESUME, which a game running elevated will refuse.</summary>
    public const uint PROCESS_SUSPEND_RESUME = 0x0800;

    [DllImport("ntdll.dll")]
    public static extern int NtSuspendProcess(IntPtr hProcess);

    [DllImport("ntdll.dll")]
    public static extern int NtResumeProcess(IntPtr hProcess);

    /// <summary>Sleep and wake, as the window hears them: suspend is about to happen; the machine
    /// is back (always); the machine is back because somebody pressed something (sometimes).</summary>
    public const int WM_POWERBROADCAST = 0x0218;
    public const int PBT_APMSUSPEND = 0x0004, PBT_APMRESUMESUSPEND = 0x0007, PBT_APMRESUMEAUTOMATIC = 0x0012;

    /// <summary>The session locking and unlocking, for a wake that has to wait for the sign-in.</summary>
    public const int WM_WTSSESSION_CHANGE = 0x02B1;
    public const int WTS_SESSION_LOCK = 0x7, WTS_SESSION_UNLOCK = 0x8;
    public const int NOTIFY_FOR_THIS_SESSION = 0;

    [DllImport("wtsapi32.dll", SetLastError = true)]
    public static extern bool WTSRegisterSessionNotification(IntPtr hWnd, int dwFlags);

    [DllImport("wtsapi32.dll")]
    public static extern bool WTSUnRegisterSessionNotification(IntPtr hWnd);

    /// <summary>The console display's state -- off, on, dimmed -- as a power setting notification:
    /// WM_POWERBROADCAST with PBT_POWERSETTINGCHANGE and a POWERBROADCAST_SETTING (the GUID, a
    /// length, then the byte). Registered on the main window for RestService.OnDisplayState.</summary>
    public const int PBT_POWERSETTINGCHANGE = 0x8013;
    public static readonly Guid GUID_CONSOLE_DISPLAY_STATE = new("6fe69556-704a-47a0-8f24-c28d936fda47");
    public const int DEVICE_NOTIFY_WINDOW_HANDLE = 0;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr RegisterPowerSettingNotification(IntPtr hRecipient, ref Guid powerSettingGuid, int flags);

    [DllImport("user32.dll")]
    public static extern bool UnregisterPowerSettingNotification(IntPtr handle);
}

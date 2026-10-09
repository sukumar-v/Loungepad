using System.IO;
using System.Windows;
using System.Windows.Interop;
using Loungepad.Interop;
using Loungepad.Models;
using Loungepad.Services;
using Microsoft.Web.WebView2.Core;

namespace Loungepad;

public partial class MainWindow : Window
{
    private readonly bool _windowed;
    private readonly SettingsStore _settings = new();
    private readonly LibraryStore _library = new();
    private readonly DisplayService _displays = new();
    private readonly LibraryScanner _scanner = new();
    private readonly VirtualKeyboardService _keyboard;
    private readonly GameLaunchService _launcher;
    private readonly GamepadService _gamepad;
    private readonly ActionService _actions;
    private readonly HidGamepadReader _hid = new();
    private readonly SteamForeground _steam = new();
    private readonly ServiceInputClient _serviceInput = new();
    private bool _inputClosing;
    /// <summary>When the keyboard and mouse were last pressed, scrolled or really moved, for rest
    /// mode's idle timer and its wake. See UserInputWatch for why GetLastInputInfo could not do it.</summary>
    private readonly UserInputWatch _input = new();
    private IntPtr _displayStateNotification;
    private readonly CursorService _cursor;
    private readonly ThemeService _themes = new();
    /// <summary>Community themes and extensions (docs/ADDONS.md): the catalogue and installer, and
    /// the runtime that keeps the enabled extensions running in hidden WebViews of their own.</summary>
    private readonly AddonService _addons;
    private readonly ExtensionRuntime _extensions;
    private readonly WindowService _windows;
    private readonly UpdateService _updates;
    private readonly RestService _rest;
    private readonly System.Windows.Threading.DispatcherTimer _restTimer;
    private TrayIcon? _tray;
    private bool _overlayWasMinimized;
    private bool _overlayActive;
    private IntPtr _overlayTarget;
    /// <summary>Whether <see cref="_overlayTarget"/> was captured by the overlay now open. It is
    /// kept across opens, so when the launcher itself was in front it still names whatever was
    /// last behind a menu -- which the action wheel must not send anything to.</summary>
    private bool _overlayTargetLive;
    private UiBridge? _bridge;
    private IntPtr _hwnd;
    private bool _suppressRefocus;

    public MainWindow(bool windowed)
    {
        _windowed = windowed;
        InitializeComponent();

        // NOT AllowsTransparency. It made the radial menu float over the desktop, but WPF
        // implements it as a layered window (WS_EX_LAYERED) and the hosted WebView2 then never
        // receives mouse or wheel messages at all — no hover, no clicks, no scrolling, with only
        // the gamepad's own bridge still working. Verified side by side against the same build:
        // opaque highlights the tile under the pointer, layered does not. The overlay menus paint
        // their own dark wash instead, which at the opacity they use looks near enough the same.

        _settings.Load();
        InputLog.Sink = Log.Info;
        _library.Load();
        _addons = AddonService.ForApp(() => _settings.Settings.AddonsIndexUrl,
            // The counts live on the metadata service (api.loungepad.app/v1/addons), so they follow
            // its endpoint setting; emptying that turns them off along with the service itself.
            () => string.IsNullOrWhiteSpace(_settings.Settings.MetadataEndpoint) ? MetadataProxyClient.DefaultEndpoint : _settings.Settings.MetadataEndpoint);
        _addons.Load();
        _addons.Sweep();
        _extensions = new ExtensionRuntime(_addons, _library, () => _settings.Settings, Dispatcher, Paths.ExtensionDataDir);

        _keyboard = new VirtualKeyboardService(_settings);
        _cursor = new CursorService(_settings);
        _updates = new UpdateService(() => _settings.Settings.AutoUpdate);
        _windows = new WindowService(_displays);
        _launcher = new GameLaunchService(_displays, _settings, _library);
        _gamepad = new GamepadService(_settings,
            // An open overlay menu owns the pad whoever Windows thinks is in front. If the
            // foreground could not be taken off the game (see TakeForeground), the menu was on
            // screen but the pad still went to the game -- a menu that looked frozen until a
            // mouse click handed Windows' foreground over.
            isLauncherForeground: () => _overlayActive || NativeMethods.GetForegroundWindow() == _hwnd,
            // Steam's Big Picture reads the pad itself, and so does any game Steam started: both
            // count as a focused game, so the stick is not also a mouse and A not also a click.
            isGameFocused: () => !_overlayActive && (_launcher.IsGameForeground()
                || (_settings.Settings.SteamOwnsPad && _steam.Owner() is not null)),
            hid: _hid);
        _gamepad.ServiceInput = _serviceInput;
        _serviceInput.ConnectionChanged += connected => Dispatcher.BeginInvoke(() =>
        {
            if (_inputClosing) return;
            if (connected) _hid.Dispose();
            else if (_hwnd != IntPtr.Zero)
            {
                _hid.Dispose();
                _hid.Register(_hwnd);
                if (_gamepad.Suspended) _hid.SetQuiet(true);
            }
        });
        _serviceInput.StatusChanged += () => Dispatcher.BeginInvoke(() => _bridge?.PushSecureInput());
        _actions = new ActionService(() => _library.Emulators);
        _actions.Load();
        _gamepad.Actions = _actions;
        _gamepad.Captured += combo => Dispatcher.BeginInvoke(() => _bridge?.PushActionCaptured(combo));
        _gamepad.CaptureRejected += why => Dispatcher.BeginInvoke(() => _bridge?.PushActionCaptureRejected(why));

        _launcher.GameStarted += _ => Dispatcher.Invoke(OnGameStarted);
        _launcher.GameExited += _ => Dispatcher.Invoke(OnGameExited);
        // The family rides along with every press, so the page never draws a legend for a pad
        // other than the one that was just used.
        _gamepad.UiEvent += name => Dispatcher.BeginInvoke(() => _bridge?.PushPadEvent(name, _gamepad.ActiveLayout));
        _gamepad.PadUsed += (layout, padName) => Dispatcher.BeginInvoke(() =>
        {
            _bridge?.PushPadLayout(layout, padName);
            _kb?.SetLayout(layout);
            _padFamilyWatcher?.Invoke(layout);
        });
        _gamepad.ConnectedChanged += c => Dispatcher.BeginInvoke(() => _bridge?.PushPadConnected(c));
        _gamepad.TouchClick += () => Dispatcher.BeginInvoke(() => _bridge?.PushPadClick());
        _gamepad.UiScroll += v => Dispatcher.BeginInvoke(() => _bridge?.PushStickScroll(v));
        _gamepad.KeyboardToggleRequested += () => Dispatcher.BeginInvoke(() => _keyboard.Toggle());
        // On the pad's thread, like the combo always was: SendInput needs no window, and the key
        // should land before the moment has passed.
        _gamepad.ScreenshotRequested += TakeScreenshot;
        _gamepad.MinimizeToggleRequested += () => Dispatcher.BeginInvoke(OnComboTap);
        _gamepad.RadialRequested += () => Dispatcher.BeginInvoke(() => _ = ShowOverlay("radial"));
        _gamepad.WheelTapRequested += () => Dispatcher.BeginInvoke(OnWheelTap);
        _gamepad.StickDirection += (x, y) => Dispatcher.BeginInvoke(() => _bridge?.PushStick(x, y));

        // Rest mode (see RestService): everything it does to the machine goes through here.
        _rest = new RestService(() => _settings.Settings, new RestService.Ports
        {
            UserPressAgeMs = () => _input.PressAgeMs,
            UserActivityAgeMs = () => _input.ActivityAgeMs,
            PadInputAgeMs = () => _gamepad.PadInputAgeMs,
            GameRunning = () => _launcher.GameRunning,
            // Chromium holds the display for the launcher's own trailers, so a hold only counts
            // as somebody watching something when another window is in front.
            DisplayHeldAwake = () => NativeMethods.GetForegroundWindow() != _hwnd && RestService.SomethingHoldsDisplay(),
            SleepAllowed = () => { try { return NativeMethods.IsPwrSuspendAllowed(); } catch { return false; } },
            DisplaysOff = () => _windows.BlankDisplays(),
            DisplaysOn = () => _windows.WakeDisplays(),
            PauseGame = () => _launcher.Pause(),
            ResumeGame = () => _launcher.Resume(),
            // Off the UI thread: the call returns only once the machine is back.
            SleepPc = () => Task.Run(() =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                bool ok = NativeMethods.SetSuspendState(false, false, false);
                int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                Log.Info(ok ? $"Rest: SetSuspendState returned after {sw.ElapsedMilliseconds} ms (the machine is back)"
                            : $"Rest: SetSuspendState was refused after {sw.ElapsedMilliseconds} ms (error {err})");
                return ok;
            }),
            WakeSource = () => Task.Run(WakeInfo.LastWakeSource),
            // Inert: the loop only wakes on a press. Quiet: the pads are read directly rather than
            // through Raw Input, or Windows counts a DualSense's stream as input and relights the
            // screen within a millisecond (see HidGamepadReader.SetQuiet).
            PadInert = v => { _gamepad.Suspended = v; _hid.SetQuiet(v); },
            Prepare = PrepareForRest,
            // The wake nudges real mouse input, which would otherwise land the UI back in
            // pointer mode with nothing highlighted.
            AfterWake = () => _gamepad.ResetInputMode(),
            Toast = m => _bridge?.PushToast(m),
        });
        _rest.Changed += () => _bridge?.PushRest();
        _launcher.PausedChanged += _ => Dispatcher.BeginInvoke(() => _bridge?.PushGameState());
        // The first process of the game was found: the page's "starting" becomes "running".
        _launcher.ProcessTracked += () => Dispatcher.BeginInvoke(() => _bridge?.PushGameState());
        // A press while resting: the loop swallows it and reports it here.
        _gamepad.WakeRequested += () => Dispatcher.BeginInvoke(() => _rest.Wake("the controller"));
        _restTimer = new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(500),
            System.Windows.Threading.DispatcherPriority.Background, (_, _) => _rest.Tick(), Dispatcher);

        _gamepad.BatteryChanged += b => Dispatcher.BeginInvoke(() => _bridge?.PushBattery(b));
        _gamepad.InputModeChanged += mode => Dispatcher.BeginInvoke(() =>
        {
            _cursor.SetPadMode(mode == "pad");
            _bridge?.PushInputMode(mode);
            // The keyboard follows the same pad/pointer split as the launcher UI.
            _kb?.SetInputMode(mode);
        });

        _keyboard.BuiltinShow = ShowBuiltinKeyboard;
        _keyboard.BuiltinHide = HideBuiltinKeyboard;
        _keyboard.BuiltinVisible = () => _kb is { IsVisible: true };
        _gamepad.KeyboardInput += what => Dispatcher.BeginInvoke(() => OnKeyboardInput(what));
        _gamepad.KeyboardArmed = () => _kb?.Armed ?? true;

        SourceInitialized += OnSourceInitialized;
        Loaded += async (_, _) => await InitWebViewAsync();
        Deactivated += OnDeactivated;
        // Keyboard focus into the page whenever the window is the active one. Nothing in the page
        // is focused otherwise and keydown never fires -- which is why a keyboard used to do
        // nothing in the launcher at all. WebView2 only takes focus through the control's own
        // Focus(), never through a Win32 SetFocus on its render window.
        Activated += (_, _) => { BackOnTop(); FocusPageIfShown(); };
        // The keyboard is a second top-level window, and WPF shuts down on the last one closing,
        // so leaving it open would keep the process alive with no UI.
        Closed += (_, _) =>
        {
            _inputClosing = true;
            _restTimer.Stop();
            // Never leave a game frozen behind an exiting launcher: nothing else could thaw it.
            _launcher.Resume();
            _hid.SetQuiet(false);
            if (_displayStateNotification != IntPtr.Zero) NativeMethods.UnregisterPowerSettingNotification(_displayStateNotification);
            if (_hwnd != IntPtr.Zero) NativeMethods.WTSUnRegisterSessionNotification(_hwnd);
            _kb?.Close(); _bridge?.Shutdown(); _extensions.Dispose(); _gamepad.Dispose(); _serviceInput.Dispose(); _hid.Dispose(); _cursor.Dispose(); _tray?.Dispose(); _updates.Dispose();
        };

        try
        {
            _tray = new TrayIcon(show: Unpark, hide: HideLauncher, shown: () => !_parked && IsVisible,
                update: UpdateFromTray, quit: ExitApp);
            _tray.SetUpdate(_updates.Status);
        }
        catch (Exception ex) { Log.Info($"No tray icon: {ex.Message}"); }
        _updates.Changed += s => Dispatcher.BeginInvoke(() =>
        {
            _tray?.SetUpdate(s);
            _bridge?.PushUpdate(s);
        });
        _updates.Start();
    }

    public void CheckForUpdates() => _ = _updates.CheckAsync(userAsked: true);

    /// <summary>
    /// Download the update if that has not happened yet, install it and restart on it. Refused
    /// while a game is running: this process is what tracks the session, and the copy that
    /// replaces it would not know there was one.
    /// </summary>
    public async void InstallUpdate()
    {
        if (_launcher.GameRunning)
        {
            _bridge?.PushToast("Close the game first: installing the update restarts Loungepad");
            return;
        }
        if (_updates.Status.State == "available") await _updates.DownloadAsync(userAsked: true);
        if (_updates.Status.State != "ready") return;
        if (_updates.InstallAndRestart(App.RestartArgs)) ExitApp();
    }

    private void UpdateFromTray()
    {
        if (_updates.Status.State is "available" or "ready") InstallUpdate();
        else CheckForUpdates();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        _windows.SetOwnWindow(_hwnd);
        PositionOnTargetDisplay();
        // Non-XInput pads report through this window as WM_INPUT, whoever is in front (see
        // HidGamepadReader). Registered here because it needs the HWND, and before the poll
        // loop starts so the first snapshot already knows what is attached.
        _hid.Register(_hwnd);
        _input.Register(_hwnd);
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        // Lock and unlock, so a wake that lands on the lock screen can wait for the sign-in.
        if (!NativeMethods.WTSRegisterSessionNotification(_hwnd, NativeMethods.NOTIFY_FOR_THIS_SESSION))
            Log.Info("Session lock notifications are not available; a wake will not wait for the sign-in");
        // The display coming on by itself while resting is what rest mode dims again.
        var displayState = NativeMethods.GUID_CONSOLE_DISPLAY_STATE;
        _displayStateNotification = NativeMethods.RegisterPowerSettingNotification(_hwnd, ref displayState, NativeMethods.DEVICE_NOTIFY_WINDOW_HANDLE);
        _gamepad.Start();
        _serviceInput.Start(_settings.Settings);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Not marked handled: WM_INPUT has to reach DefWindowProc so the system can release
        // the buffer behind it. Read once; HID reports go to the pad reader, the keyboard and
        // mouse to the input watch.
        if (msg == HidNative.WM_INPUT)
        {
            var (buffer, size) = _input.Read(lParam);
            if (size > 0)
            {
                if (BitConverter.ToUInt32(buffer, 0) == HidNative.RIM_TYPEHID) _hid.OnInputData(buffer, size);
                else _input.OnInputData(buffer, size);
            }
        }
        else if (msg == HidNative.WM_INPUT_DEVICE_CHANGE) _hid.OnDeviceChange(wParam, lParam);
        else if (msg == NativeMethods.WM_POWERBROADCAST)
        {
            int what = (int)wParam.ToInt64();
            if (what == NativeMethods.PBT_POWERSETTINGCHANGE)
            {
                // POWERBROADCAST_SETTING: the GUID (16 bytes), DataLength (4), then the data.
                if (lParam != IntPtr.Zero && new Guid(ReadBytes(lParam, 16)) == NativeMethods.GUID_CONSOLE_DISPLAY_STATE
                    && System.Runtime.InteropServices.Marshal.ReadInt32(lParam, 16) >= 1)
                    _rest.OnDisplayState(System.Runtime.InteropServices.Marshal.ReadByte(lParam, 20) != 0);
            }
            else _rest.OnPowerEvent(what);
        }
        else if (msg == NativeMethods.WM_WTSSESSION_CHANGE)
        {
            var what = (int)wParam.ToInt64();
            if (what == NativeMethods.WTS_SESSION_LOCK) _rest.OnSessionLock(true);
            else if (what == NativeMethods.WTS_SESSION_UNLOCK) _rest.OnSessionLock(false);
        }
        return IntPtr.Zero;
    }

    private static byte[] ReadBytes(IntPtr p, int n)
    {
        var b = new byte[n];
        System.Runtime.InteropServices.Marshal.Copy(p, b, 0, n);
        return b;
    }

    /// <summary>The page gets the keyboard whenever the launcher is the window in front.</summary>
    private void FocusPageIfShown()
    {
        if (_parked || !IsActive) return;
        WebView.Focus();
    }

    /// <summary>Place the window fullscreen on the configured TV display (pixel-exact via SetWindowPos).</summary>
    public void PositionOnTargetDisplay()
    {
        if (_hwnd == IntPtr.Zero) return;

        var target = (_settings.Settings.TvDeviceName is { } name ? _displays.GetDisplay(name) : null)
                     ?? _displays.GetDisplays().FirstOrDefault(d => d.IsPrimary)
                     ?? _displays.GetDisplays().FirstOrDefault();
        if (target is null) return;

        if (_windowed)
        {
            NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_NOTOPMOST,
                target.X + 80, target.Y + 80, 1280, 720, NativeMethods.SWP_SHOWWINDOW);
            return;
        }

        Topmost = true;
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST,
            target.X, target.Y, target.Width, target.Height, NativeMethods.SWP_SHOWWINDOW);
    }

    private async Task InitWebViewAsync()
    {
        try
        {
            // Trailers start on their own once the highlight has rested on a game, and Chromium
            // only lets a MUTED video do that without a click or a key first. A gamepad press
            // arrives as a bridge message, which is not a user gesture as far as the autoplay
            // policy is concerned, so with "Trailer sound" on the play() would be refused and the
            // screen would simply stay still. The flag lifts the policy for this one browser.
            var options = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required",
            };
            var env = await CoreWebView2Environment.CreateAsync(null, Paths.WebViewDir, options);
            await WebView.EnsureCoreWebView2Async(env);
        }
        catch (WebView2RuntimeNotFoundException ex)
        {
            // The whole UI is a web page, so there is nothing to fall back to. Windows 11 ships
            // the runtime, but a fresh Windows 10 box may not have it, and without this the window
            // just sits there black -- the exception would be swallowed by the dispatcher handler.
            Log.Info($"WebView2 runtime missing: {ex.Message}");
            MessageBox.Show(
                "Loungepad needs the Microsoft Edge WebView2 Runtime, which is not installed.\n\n" +
                "Install the free Evergreen Runtime from\n" +
                "https://developer.microsoft.com/microsoft-edge/webview2/\n\n" +
                "then start Loungepad again.",
                "Loungepad", MessageBoxButton.OK, MessageBoxImage.Error);
            Application.Current.Shutdown();
            return;
        }

        var core = WebView.CoreWebView2;
        // Match the page so there is never a flash of the WebView2 default white on startup.
        WebView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x08, 0x08, 0x0A);
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        // The page has the keyboard now, so the browser's own shortcuts have to go: F5 would
        // reload the launcher and Ctrl+F would open a find bar over it.
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.NavigationCompleted += (_, _) => FocusPageIfShown();
#if !DEBUG
        core.Settings.AreDevToolsEnabled = false;
#endif

        // The page is built into the exe. A build whose csproj stopped embedding it would open to
        // a black screen with nothing in the log to say why.
        if (!ShippedFiles.Exists("ui/index.html"))
        {
            Log.Info("This build has no ui/index.html embedded; nothing to show");
            MessageBox.Show("This copy of Loungepad was built without its interface. Download it again from " +
                            $"https://github.com/{UpdateService.Repo}/releases/latest",
                "Loungepad", MessageBoxButton.OK, MessageBoxImage.Error);
            Application.Current.Shutdown();
            return;
        }
        ServeShipped(core);
        ServeDataFolder(core);

        // The page is the trust root for every bridge command, so the top frame must never be
        // anything but it. A cross-origin frame can set top.location with a user activation, and a
        // pad press is one; a target=_blank link in an embed would open a bare WebView2 window on
        // the TV. Both are refused here rather than left to the page.
        core.NavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith("https://loungepad.ui/", StringComparison.OrdinalIgnoreCase))
            {
                Log.Info($"Refused a navigation to {e.Uri}");
                e.Cancel = true;
            }
        };
        core.NewWindowRequested += (_, e) => e.Handled = true;

        _bridge = new UiBridge(this, core, _settings, _library, _displays, _scanner, _launcher, _keyboard, _windows, _themes, _updates, _addons, _extensions);
        // Saving a theme file should show up in the launcher, not after a restart.
        _themes.Changed += () => Dispatcher.BeginInvoke(() => _bridge?.PushThemes());
        _themes.Watch();
        core.WebMessageReceived += _bridge.OnWebMessageReceived;

        core.Navigate("https://loungepad.ui/index.html");
    }

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".css"] = "text/css", [".js"] = "text/javascript", [".json"] = "application/json",
        [".html"] = "text/html", [".txt"] = "text/plain", [".svg"] = "image/svg+xml",
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif", [".webp"] = "image/webp", [".avif"] = "image/avif",
        [".ico"] = "image/x-icon", [".mp4"] = "video/mp4", [".webm"] = "video/webm",
        [".woff"] = "font/woff", [".woff2"] = "font/woff2", [".ttf"] = "font/ttf", [".otf"] = "font/otf",
    };

    /// <summary>
    /// The hosts the app's own pages come from, and the folder of shipped files behind each.
    ///
    /// The YouTube player is a page of its own on a second origin (ui/player, see player.js):
    /// YouTube's script runs there with no bridge and no data host to reach, and the launcher page
    /// drives it through postMessage. Its folder is NOT served on the page's host as well: the
    /// player's policy lets YouTube's script run, and a copy framed on the page's own origin would
    /// be that script with the bridge one `parent` away.
    /// </summary>
    private static readonly Dictionary<string, string> ShippedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["loungepad.ui"] = "ui/",
        ["loungepad.player"] = "ui/player/",
    };

    private static readonly HashSet<string> TextTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ".html", ".css", ".js", ".json", ".svg", ".txt",
    };

    /// <summary>
    /// Serve https://loungepad.ui/... and https://loungepad.player/... out of the exe (see
    /// <see cref="ShippedFiles"/>). They used to be folder mappings onto ui\ beside the exe, which
    /// is what kept a release from being one file.
    ///
    /// No Access-Control-Allow-Origin on either host, so a fetch from one to the other is refused
    /// both ways -- what DenyCors gave the player -- while the page can still frame the player and
    /// load its own scripts. Checked in a scratch WebView2 (runtime 154): the frame starts and
    /// messages its parent, and both cross-origin fetches fail.
    /// </summary>
    private static void ServeShipped(CoreWebView2 core)
    {
        foreach (var host in ShippedHosts.Keys)
        {
            // The overload with source kinds, not the plain one: the plain filter never saw the
            // requests a cross-site frame makes for its own scripts, so the player's page arrived
            // and its player.js went out to a network that has no such host.
            core.AddWebResourceRequestedFilter($"https://{host}/*", CoreWebView2WebResourceContext.All,
                CoreWebView2WebResourceRequestSourceKinds.Document);
        }
        core.WebResourceRequested += (_, e) =>
        {
            if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri)
                || !ShippedHosts.TryGetValue(uri.Host, out var folder)) return;
            try
            {
                var rel = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
                // Neither the player nor the extension runtime is served on the page's own origin:
                // each is a page meant for a second origin (see ExtensionHost for ext/).
                var allowed = rel.Length > 0
                    && !rel.Split('/').Any(s => s is "" or "." or ".." || s.Contains('\\'))
                    && !(folder == "ui/" && (rel.StartsWith("player/", StringComparison.OrdinalIgnoreCase)
                                             || rel.StartsWith("ext/", StringComparison.OrdinalIgnoreCase)));
                var body = allowed ? ShippedFiles.ReadAllBytes(folder + rel) : null;
                if (body is null)
                {
                    e.Response = core.Environment.CreateWebResourceResponse(null, 404, "Not Found", "");
                    return;
                }
                var ext = Path.GetExtension(rel);
                var type = ContentTypes.TryGetValue(ext, out var t) ? t : "application/octet-stream";
                if (TextTypes.Contains(ext)) type += "; charset=utf-8";
                // no-store: these change with the exe, and an update must never be shown a page
                // the WebView kept from the version before.
                e.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(body), 200, "OK",
                    $"Content-Type: {type}\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff");
            }
            catch (Exception ex)
            {
                Log.Info($"Serving {e.Request.Uri} failed: {ex.Message}");
                e.Response = core.Environment.CreateWebResourceResponse(null, 500, "Error", "");
            }
        };
    }

    /// <summary>
    /// Serve https://loungepad.data/... out of the data folder, by hand.
    ///
    /// SetVirtualHostNameToFolderMapping cannot do it: the folder is read by WebView2's own
    /// sandboxed process, and it will not serve anything under %APPDATA%. The mapping is
    /// accepted without complaint and then every single request to that host fails, which is
    /// why cover art has never appeared from disk -- the placeholder initials looked like a
    /// design choice rather than a broken host. Reading the file here instead puts it in this
    /// process, which has no such restriction, and fixes covers and themes in one go.
    /// </summary>
    /// <summary>
    /// Top-level names on the data host that live somewhere other than the data folder. The
    /// trailer cache is gigabytes of video and belongs under Local, not in a roaming profile, so
    /// it is served from there under the same host rather than given a second one.
    /// </summary>
    private static readonly Dictionary<string, string> DataRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        ["trailers"] = Paths.TrailersDir,
        ["achievements"] = Paths.AchievementIconsDir,
        // The add-on icons: what the repository lists and what is installed, copied out of each
        // folder (AddonService). The extension folders themselves are never served.
        ["addons"] = Paths.AddonsCacheDir,
    };

    /// <summary>
    /// The folders under the data folder the page may read. Everything else there -- settings.json
    /// with its keys, library.json, the log, the account tokens, the owned-games list -- is the
    /// host's and is never served. The host used to answer for the whole folder, which put every
    /// secret one fetch away from anything running in the WebView.
    /// </summary>
    private static readonly HashSet<string> ServedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "covers", "themes",
    };

    /// <summary>The one origin the data host answers to. It was `*`, which let any frame inside the
    /// WebView -- a video embed, say -- read covers and, before <see cref="ServedFolders"/>, the
    /// settings file.</summary>
    private const string PageOrigin = "https://loungepad.ui";

    /// <summary>Whether one of the pad-driven overlays (the Power Wheel, the in-game menu) is up
    /// in front of whatever was running. The bridge only pushes live hardware readings then.</summary>
    public bool OverlayActive => _overlayActive;

    /// <summary>The window's handle, made if it has not been yet: the extensions' hidden WebViews
    /// are children of it (ExtensionHost), never shown and never sized.</summary>
    public IntPtr WindowHandle => _hwnd != IntPtr.Zero ? _hwnd : new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();

    /// <summary>Files up to this size are read whole; anything larger is streamed off disk.</summary>
    private const long ReadWholeBelow = 8 * 1024 * 1024;

    private static void ServeDataFolder(CoreWebView2 core)
    {
        core.AddWebResourceRequestedFilter("https://loungepad.data/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) =>
        {
            // Every handler hears every filtered request, and the shipped hosts have their own.
            if (!e.Request.Uri.StartsWith("https://loungepad.data/", StringComparison.OrdinalIgnoreCase)) return;
            try
            {
                var uri = new Uri(e.Request.Uri);
                // Query is only ever a cache-busting stamp; the path alone names the file.
                var rel = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
                var slash = rel.IndexOf('/');
                var head = slash < 0 ? rel : rel[..slash];
                string baseDir;
                if (DataRoots.TryGetValue(head, out var other))
                {
                    baseDir = other;
                    rel = slash < 0 ? "" : rel[(slash + 1)..];
                }
                else if (ServedFolders.Contains(head))
                {
                    baseDir = Path.Combine(Paths.DataDir, head);
                    rel = slash < 0 ? "" : rel[(slash + 1)..];
                }
                else
                {
                    // A file at the root of the data folder, or a folder that is not on the list.
                    e.Response = core.Environment.CreateWebResourceResponse(null, 404, "Not Found", "");
                    return;
                }
                var full = Path.GetFullPath(Path.Combine(baseDir, rel.Replace('/', Path.DirectorySeparatorChar)));

                // Refuse anything that resolves outside the folder, so a crafted path in a theme
                // cannot read the rest of the disk.
                var root = Path.GetFullPath(baseDir) + Path.DirectorySeparatorChar;
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
                {
                    e.Response = core.Environment.CreateWebResourceResponse(null, 404, "Not Found", "");
                    return;
                }

                var type = ContentTypes.TryGetValue(Path.GetExtension(full), out var t) ? t : "application/octet-stream";
                // Allow-Origin because the page is served from loungepad.ui: without it a theme
                // could not fetch its own JSON, and fonts would be refused outright. That one
                // origin, not `*`: nothing else inside the WebView has any business here.
                var headers = $"Content-Type: {type}\r\nAccess-Control-Allow-Origin: {PageOrigin}\r\nCache-Control: no-cache\r\nAccept-Ranges: bytes";
                var length = new FileInfo(full).Length;

                // A <video> asks for the file in pieces -- the first few hundred KB, then the
                // index at the end of an mp4 that is not fast-start, then the rest as it plays --
                // and expects 206 with the byte range it got. Answering 200 with the whole file
                // every time still plays, at the cost of the whole 200 MB being read for each
                // request. Only the one form a browser sends is handled: "bytes=start-" and
                // "bytes=start-end".
                var range = e.Request.Headers.Contains("Range") ? e.Request.Headers.GetHeader("Range") : null;
                if (range is not null && ParseRange(range, length) is { } r)
                {
                    var (start, end) = r;
                    if (start >= length)
                    {
                        e.Response = core.Environment.CreateWebResourceResponse(null, 416, "Range Not Satisfiable",
                            $"Content-Range: bytes */{length}");
                        return;
                    }
                    var count = end - start + 1;
                    var slice = new SliceStream(OpenShared(full), start, count);
                    e.Response = core.Environment.CreateWebResourceResponse(slice, 206, "Partial Content",
                        headers + $"\r\nContent-Range: bytes {start}-{end}/{length}\r\nContent-Length: {count}");
                    return;
                }

                // Small files -- every cover, every theme file -- are read whole as before: one
                // read, no handle left open. Only a video is worth streaming.
                Stream body = length < ReadWholeBelow
                    ? new MemoryStream(File.ReadAllBytes(full))
                    : OpenShared(full);
                e.Response = core.Environment.CreateWebResourceResponse(body, 200, "OK",
                    headers + $"\r\nContent-Length: {length}");
            }
            catch (Exception ex)
            {
                Log.Info($"Serving {e.Request.Uri} failed: {ex.Message}");
                e.Response = core.Environment.CreateWebResourceResponse(null, 500, "Error", "");
            }
        };
    }

    /// <summary>Read-only, and shared for everything including delete: WebView2 releases the
    /// stream when it is done with the response, and the cache must be able to evict a file that
    /// a response is still holding.</summary>
    private static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.Asynchronous);

    /// <summary>"bytes=a-b" or "bytes=a-" to an inclusive (start, end) inside the file, or null.</summary>
    private static (long Start, long End)? ParseRange(string header, long length)
    {
        if (!header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return null;
        var spec = header[6..].Trim();
        if (spec.Contains(',')) return null;   // several ranges: not something a browser sends for media
        var dash = spec.IndexOf('-');
        if (dash <= 0) return null;            // a suffix range ("-500") is not sent for media either
        if (!long.TryParse(spec[..dash], out var start) || start < 0) return null;
        var end = length - 1;
        if (dash < spec.Length - 1)
        {
            if (!long.TryParse(spec[(dash + 1)..], out end) || end < start) return null;
            end = Math.Min(end, length - 1);
        }
        return (start, end);
    }

    /// <summary>A window onto part of a stream, for a 206. Disposing it closes the file.</summary>
    private sealed class SliceStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _start, _length;
        private long _pos;

        public SliceStream(Stream inner, long start, long length)
        {
            _inner = inner; _start = start; _length = length;
            _inner.Seek(start, SeekOrigin.Begin);
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position
        {
            get => _pos;
            set { _pos = Math.Clamp(value, 0, _length); _inner.Seek(_start + _pos, SeekOrigin.Begin); }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var left = _length - _pos;
            if (left <= 0) return 0;
            var n = _inner.Read(buffer, offset, (int)Math.Min(count, left));
            _pos += n;
            return n;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            var left = _length - _pos;
            if (left <= 0) return 0;
            var n = await _inner.ReadAsync(buffer.AsMemory(offset, (int)Math.Min(count, left)), ct);
            _pos += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _pos + offset,
                _ => _length + offset,
            };
            return _pos;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }

    /// <summary>
    /// Out of the way entirely, rather than minimized.
    ///
    /// The window sets ShowInTaskbar=false, so there is no taskbar button to minimize into and
    /// Windows falls back to the legacy minimized stub -- the little titled bar that was appearing
    /// in the bottom-left corner. Hiding takes the window off the screen, out of Alt-Tab and out
    /// of the z-order, which is what "park the launcher" always meant. The HWND and the WebView2
    /// survive, so coming back is instant and the UI keeps its state.
    /// </summary>
    private bool _parked;

    public void Park()
    {
        _parked = true;
        _suppressRefocus = true;
        Topmost = false;
        Hide();
    }

    /// <summary>Bring it back to the TV and to the foreground.</summary>
    public void Unpark()
    {
        _parked = false;
        _suppressRefocus = false;
        Show();                      // PositionOnTargetDisplay restores Topmost for the TV
        PositionOnTargetDisplay();
        Activate();
        TakeForeground();
    }

    /// <summary>
    /// SetForegroundWindow, in the form Windows actually grants.
    ///
    /// Windows only lets a program take the foreground if it received the last input -- and a
    /// controller read through XInput is not input as far as that rule is concerned. So with a
    /// game in front, the plain call is refused: the window shows (it is topmost), but the game
    /// keeps the foreground and the keyboard focus, which is why the Guide menu needed a mouse
    /// click before anything worked. Joining the foreground window's input queue for the length
    /// of the call is the sanctioned way round it; the two queues are separated again at once.
    /// </summary>
    private void TakeForeground()
    {
        var fg = NativeMethods.GetForegroundWindow();
        if (fg == _hwnd) return;
        if (NativeMethods.SetForegroundWindow(_hwnd) && NativeMethods.GetForegroundWindow() == _hwnd) return;

        var fgThread = fg == IntPtr.Zero ? 0 : NativeMethods.GetWindowThreadProcessId(fg, out _);
        var ours = NativeMethods.GetCurrentThreadId();
        var attached = fgThread != 0 && fgThread != ours && NativeMethods.AttachThreadInput(ours, fgThread, true);
        try
        {
            NativeMethods.BringWindowToTop(_hwnd);
            NativeMethods.SetForegroundWindow(_hwnd);
            Activate();
        }
        finally
        {
            if (attached) NativeMethods.AttachThreadInput(ours, fgThread, false);
        }
        if (NativeMethods.GetForegroundWindow() != _hwnd)
            Log.Info("Could not take the foreground from the game; the menu still owns the pad");
    }

    private void OnGameStarted()
    {
        Park();
        _bridge?.PushGameState();
    }

    private void OnGameExited()
    {
        _overlayActive = false;
        _gamepad.MenuOwnsStick = false;
        _gamepad.ResetInputMode();
        Unpark();
        _bridge?.PushGameState();
        _bridge?.PushState(); // refresh playtime/last-played shown in the UI
    }

    /// <summary>
    /// Drop always-on-top around a modal file dialog. Without this the dialog opens *behind* the
    /// full-screen launcher and looks like nothing happened.
    /// </summary>
    /// <summary>
    /// Keyboard focus into the WebView. WPF's Focus() on the control is what calls the
    /// controller's MoveFocus, which is the only thing WebView2 accepts -- a Win32 SetFocus on
    /// its render window is ignored. Needed only when a text field is about to be typed into.
    /// </summary>
    public void FocusPage()
    {
        if (_parked) return;
        Activate();
        WebView.Focus();
    }

    /// <summary>See GamepadService.UiClaimedButtons.</summary>
    public void SetUiClaimedButtons(string[] buttons) => _gamepad.UiClaimedButtons = buttons;

    /// <summary>See GamepadService.ModalButtonHandler: a window of ours in front of the launcher
    /// that wants some of the pad's buttons, or null once it has closed.</summary>
    public void SetModalPadHandler(Func<string, bool>? handler) => _gamepad.ModalButtonHandler = handler;

    /// <summary>The family of the pad in hand -- xbox, playstation, switch, generic -- for a window
    /// of ours that draws button hints of its own.</summary>
    public string PadFamily => _gamepad.ActiveLayout;

    private Action<string>? _padFamilyWatcher;

    /// <summary>Told the pad family whenever it changes, for as long as the watcher is set.</summary>
    public void WatchPadFamily(Action<string>? watcher) => _padFamilyWatcher = watcher;

    public void BeginModalDialog()
    {
        _suppressRefocus = true;
        Topmost = false;
    }

    public void EndModalDialog()
    {
        _suppressRefocus = false;
        if (!_windowed) Topmost = true;
        Activate();
        NativeMethods.SetForegroundWindow(_hwnd);
    }

    /// <summary>Menu combo: park the launcher so the desktop is usable, and bring it back.</summary>
    public void ToggleParked()
    {
        if (_parked)
        {
            Unpark();
            _gamepad.ResetInputMode();
        }
        else
        {
            Park();
        }
    }

    /// <summary>
    /// The combo tapped in the tap-and-hold mode: the Power Wheel, from anywhere. A tap with a
    /// menu already up puts it away instead, as the guide button does on a console -- the same
    /// button in and out, whichever menu it is.
    /// </summary>
    private void OnWheelTap()
    {
        if (_overlayActive)
        {
            _bridge?.PushDismiss();
            CloseOverlay(true);
            return;
        }
        _ = ShowOverlay("radial");
    }

    /// <summary>
    /// Show or hide Loungepad: the combo held (tap and hold) or tapped (double tap). While a game
    /// is running this raises the in-game menu instead of minimizing, so the pad can reach "close
    /// game" and "home" without touching a keyboard.
    /// </summary>
    private void OnComboTap()
    {
        // A tap while a menu is up dismisses it. Without this the launcher would minimize out
        // from under an open radial, stranding MenuOwnsStick and leaving the stick-mouse dead.
        if (_overlayActive)
        {
            _bridge?.PushDismiss();
            CloseOverlay(true);
            return;
        }
        if (_launcher.GameRunning) _ = ShowOverlay("ingame");
        else ToggleParked();
    }

    /// <summary>
    /// Put Loungepad away without a pad: H on the keyboard, Minimize Loungepad in the library's hint
    /// bar, and the tray menu. The combo's hold does this from a pad, but a keyboard and mouse had
    /// no way off the screen at all -- the window is topmost on the TV and KeepFocus takes the
    /// foreground back from anything else. A menu up over another window goes with it, as it does
    /// on the combo; a game running behind the launcher is what is left in front.
    /// </summary>
    public void HideLauncher()
    {
        if (_overlayActive)
        {
            _bridge?.PushDismiss();
            CloseOverlay(true);
        }
        if (!_parked) Park();
    }

    /// <summary>
    /// Bring the launcher forward showing one of the overlay menus. The window the user was on is
    /// captured first, because showing ourselves steals the foreground and the radial menu's
    /// actions all apply to that window.
    /// </summary>
    public async Task ShowOverlay(string mode)
    {
        _overlayWasMinimized = _parked;
        var fg = NativeMethods.GetForegroundWindow();
        if (fg != _hwnd) _overlayTarget = fg;
        _overlayTargetLive = fg != _hwnd;

        // Grab the screen before we put ourselves in front of it: the menu paints this still,
        // dimmed, as its background, which is how you can still see what is behind it now that
        // the window itself is opaque.
        var shot = _windows.CaptureDisplay(_settings.Settings.TvDeviceName);

        // Tell the UI to switch to overlay mode FIRST. Script keeps running while the window is
        // hidden, so by the time we show it the library is already hidden and only the menu is
        // painted -- otherwise the launcher flashes up before the overlay appears.
        _bridge?.PushOverlay(mode, _windows.TitleOf(_overlayTarget), shot,
            _overlayTargetLive ? ActionService.ExeOf(_overlayTarget) : "",
            _overlayTargetLive && _launcher.OwnsWindow(_overlayTarget),
            overLauncher: !_overlayTargetLive);
        await Task.Delay(90);

        _overlayActive = true;
        // Both menus, not just the radial: the in-game list is pad-driven too, and letting the
        // stick move the cursor underneath it flipped the UI into pointer mode with the pointer
        // over nothing, which left A dead — the menu looked frozen.
        _gamepad.MenuOwnsStick = true;
        Unpark();
    }

    /// <summary>Dismiss an overlay, putting the launcher back where it was.</summary>
    public void CloseOverlay(bool refocusTarget)
    {
        _overlayActive = false;
        _gamepad.MenuOwnsStick = false;
        bool goBack = _overlayWasMinimized || _launcher.GameRunning;
        if (goBack)
        {
            Park();
            if (refocusTarget && _overlayTarget != IntPtr.Zero) _windows.Focus(_overlayTarget);
        }
    }

    /// <summary>
    /// A Power Wheel shortcut: the program started, Loungepad out of the way, and the program's
    /// window in front -- on the TV, when one is set.
    ///
    /// Opened over Loungepad itself, the close above leaves the launcher where it was: topmost on
    /// the TV with the new window drawn underneath it, and KeepFocus taking the foreground back
    /// 350 ms later, so the mixer opened behind the launcher and had to be switched to by hand (a
    /// handheld user's report, Oct 8 2026). It parks now, as it does when a game starts, and for
    /// the same reason as there the program is handed the foreground twice: allowed to take it
    /// while the launcher is still the foreground process -- once it is hidden, Windows refuses a
    /// program the foreground for having been started by it -- and brought forward through the
    /// input-queue join once its window is up.
    /// </summary>
    public async Task OpenShortcut(string id)
    {
        var before = WindowService.VisibleWindows();
        if (!_windows.RunShortcut(id, out var started))
        {
            CloseOverlay(false);
            _bridge?.PushToast("That shortcut could not be opened");
            return;
        }
        using (started)
        {
            try { if (started is not null) NativeMethods.AllowSetForegroundWindow((uint)started.Id); }
            catch (Exception ex) { Log.Info($"Radial: {id}: AllowSetForegroundWindow failed: {ex.Message}"); }
        }
        CloseOverlay(false);
        if (!WindowService.OpensWindow(id)) return;
        Park();

        var hwnd = await _windows.WaitForShortcutWindow(id, before, TimeSpan.FromSeconds(8), () => _parked);
        if (!_parked) return;   // brought back meanwhile: the launcher is what was asked for now
        if (hwnd == IntPtr.Zero)
        {
            Log.Info($"Radial: {id} put up no window to bring forward");
            return;
        }
        if (_settings.Settings.TvDeviceName is { } tv && _windows.DisplayOf(hwnd) != tv) _windows.MoveToDisplay(hwnd, tv);
        if (NativeMethods.GetForegroundWindow() != hwnd) WindowService.ForceForeground(hwnd);
        Log.Info($"Radial: {id} brought forward" + (NativeMethods.GetForegroundWindow() == hwnd ? "" : ", but Windows kept the foreground elsewhere"));
    }

    /// <summary>
    /// The Power Wheel's Switch window: that window on the TV and in front, and Loungepad out of its
    /// way. Over the launcher itself it used to stay up on top of the window just picked, and
    /// KeepFocus took the foreground back, exactly as with the shortcuts. The window is given the
    /// foreground before the launcher hides, while that is still the launcher's to give.
    /// </summary>
    public void SwitchTo(IntPtr hwnd)
    {
        if (_settings.Settings.TvDeviceName is { } tv && _windows.DisplayOf(hwnd) != tv) _windows.MoveToDisplay(hwnd, tv);
        WindowService.ForceForeground(hwnd);
        CloseOverlay(false);
        Park();
    }


    // ---- built-in on-screen keyboard ----

    private KeyboardWindow? _kb;

    /// <summary>
    /// Show the built-in keyboard on the TV. It is created lazily: most sessions never raise it,
    /// and a WPF window costs nothing until it exists.
    /// </summary>
    private void ShowBuiltinKeyboard()
    {
        if (_kb is null)
        {
            _kb = new KeyboardWindow();
            // Menu commits and leaves, B just leaves; both come back through here so the pad
            // is handed back and the window hidden in one place.
            _kb.CloseRequested += () => Dispatcher.BeginInvoke(HideBuiltinKeyboard);
            // A switch flipped on the keyboard's own options page. The keyboard has rebuilt itself
            // already; this only keeps settings.json and the Settings screen in step with it.
            _kb.OptionsChanged += options =>
            {
                options.WriteTo(_settings.Settings);
                _settings.Save();
                _serviceInput.UpdateProfile(_settings.Settings);
                _bridge?.PushKeyboardOptions();
            };
            // The size steps beside the gear: the same number as Settings → Keyboard → Keyboard size.
            _kb.ScaleChanged += scale =>
            {
                _settings.Settings.KeyboardScale = scale;
                _settings.Save();
                _serviceInput.UpdateProfile(_settings.Settings);
                _bridge?.PushKeyboardOptions();
            };
        }
        _kb.SetLayout(_gamepad.ActiveLayout);
        var target = (_settings.Settings.TvDeviceName is { } name ? _displays.GetDisplay(name) : null)
                     ?? _displays.GetDisplays().FirstOrDefault(d => d.IsPrimary);
        if (target is not null)
            _kb.ShowOn(target, Math.Clamp(_settings.Settings.KeyboardScale, KeyboardWindow.ScaleMin, KeyboardWindow.ScaleMax), KeyboardOptions.From(_settings.Settings));
        // Hand the pad over. Nothing else can read it until the keyboard closes, which is what
        // makes A "press this key" rather than "launch the highlighted game".
        _gamepad.KeyboardOwnsPad = true;
    }

    /// <summary>Re-apply the keyboard settings while it is up, so the size slider is live.</summary>
    public void RefreshBuiltinKeyboard()
    {
        _serviceInput.UpdateProfile(_settings.Settings);
        if (_kb is { IsVisible: true }) ShowBuiltinKeyboard();
    }

    private void HideBuiltinKeyboard()
    {
        _kb?.Hide();
        _gamepad.KeyboardOwnsPad = false;
    }

    private void OnKeyboardInput(string what)
    {
        if (_kb is null) return;
        switch (what)
        {
            case "Up": case "Down": case "Left": case "Right": _kb.Move(what); break;
            case "Press":      _kb.Press(); break;
            case "Backspace":  _kb.Backspace(); break;
            case "Space":      _kb.Space(); break;
            case "Commit":     _kb.Commit(); break;
            case "Shift":      _kb.ToggleShift(); break;
            case "CaretLeft":  _kb.CaretLeft(); break;
            case "CaretRight": _kb.CaretRight(); break;
            case "Layer":      _kb.ToggleLayer(); break;
            case "Suggest":    _kb.AcceptSuggestion(0); break;
            case "PointerClick": _kb.TargetClicked(); break;
            case "Close":
                // On the keyboard's options page, B goes back to the keys.
                if (_kb.LeaveOptions()) break;
                HideBuiltinKeyboard();
                // Over the launcher, B also leaves the text field the keyboard was typing into.
                // Over another app it only closes the keyboard.
                if (NativeMethods.GetForegroundWindow() == _hwnd) _bridge?.PushKeyboardDismissed();
                break;
        }
    }

    /// <summary>Radial opened from the in-game menu and back again.</summary>
    public void SetRadialActive(bool active) => _gamepad.MenuOwnsStick = active;

    /// <summary>
    /// Re-send the pad state once the UI is up. The controller is normally detected while the
    /// WebView is still starting, so that first connected/battery push has no bridge to cross and
    /// is lost -- the corner then showed "no controller" with one sitting right there.
    /// </summary>
    public void PushPadState()
    {
        _bridge?.PushPadConnected(_gamepad.Connected);
        _bridge?.PushBattery(_gamepad.CurrentBattery);
        _bridge?.PushPadLayout(_gamepad.ActiveLayout, _gamepad.ActiveName);
    }

    /// <summary>The UI changed input mode by itself; keep the pad service's copy in step.</summary>
    public void SetInputMode(string mode) => _gamepad.NotifyInputMode(mode);

    /// <summary>Rest mode: the idle timer, the screen, the frozen game and the sleep. See RestService.</summary>
    public RestService Rest => _rest;

    /// <summary>
    /// What a rest needs from the window first: the menu down, and the launcher on screen when
    /// no game is running, so the wake lands on it rather than on a half-focused desktop. With a
    /// game running the launcher stays parked and the wake lands on the game.
    /// </summary>
    private void PrepareForRest()
    {
        CloseOverlay(false);
        _bridge?.PushDismiss();
        if (!_launcher.GameRunning) GoHome();
    }

    /// <summary>The window the radial menu acts on (whatever was in front when it opened).</summary>
    public IntPtr OverlayTarget => _overlayTarget;

    internal ActionService Actions => _actions;
    internal InputServiceUiStatus SecureInputStatus => _serviceInput.UiStatus;
    internal Task SetSecureInput(string feature, bool enabled, bool installConfirmed) => _serviceInput.SetEnabled(feature, enabled, _settings.Settings, installConfirmed);
    internal Task UninstallSecureInput() => _serviceInput.Uninstall();
    internal Task<string> UpdateSecureInput() => _serviceInput.Update(_settings.Settings);
    internal (Version Installed, Version Target, bool InUse)? PendingSecureInputUpdate() => _serviceInput.PendingUpdate();

    /// <summary>
    /// An action chosen on the wheel: put the launcher away, hand the foreground back to the
    /// window the menu was opened over, and once it has it, press the shortcut. A shortcut that
    /// acts on the window in front is refused, with a toast, when that would be the launcher
    /// itself -- an Alt+F4 meant for a browser must never close Loungepad -- while a system-wide
    /// one (the media keys, any Win chord) goes regardless.
    /// </summary>
    public void FireAction(ActionApp app, ActionDef action)
    {
        if (string.IsNullOrEmpty(action.Keys))
        {
            _bridge?.PushToast($"{action.Name} has no shortcut yet: set one under Settings → Actions");
            return;
        }
        var target = _overlayTargetLive && _overlayTarget != IntPtr.Zero && NativeMethods.IsWindow(_overlayTarget)
            ? _overlayTarget : IntPtr.Zero;
        bool systemWide = ShortcutKeys.IsSystemWide(action.Keys);
        CloseOverlay(true);
        // Opened with the launcher on screen but another window in front (KeepFocus off): the
        // close above leaves the launcher up, so the target is asked for by name.
        if (target != IntPtr.Zero && NativeMethods.GetForegroundWindow() != target) _windows.Focus(target);

        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 25 && target != IntPtr.Zero && NativeMethods.GetForegroundWindow() != target; i++)
                await Task.Delay(20);
            var fg = NativeMethods.GetForegroundWindow();
            bool onTarget = target != IntPtr.Zero ? fg == target : fg != _hwnd;
            if (!onTarget && !systemWide)
            {
                Log.Info($"Action {action.Name}: not sent, {(fg == _hwnd ? "the launcher" : "another window")} is in front");
                _ = Dispatcher.BeginInvoke(() => _bridge?.PushToast(target == IntPtr.Zero
                    ? "Open an app in front of Loungepad first: this action works on the window in front"
                    : "Could not bring the window forward"));
                return;
            }
            await Task.Delay(60);
            try
            {
                _actions.SendKeys(action.Keys);
                Log.Info($"Action: {action.Name} → {action.Keys} sent to {ActionService.ExeOf(fg)} ({app.Name})");
            }
            catch (Exception ex) { Log.Info($"Action {action.Name} failed: {ex.Message}"); }
        });
    }

    /// <summary>
    /// The screenshot key for whatever is in front, pressed from the pad's thread. A focused Steam
    /// game gets F12, the Steam overlay's own key, so the picture lands with the game's Steam
    /// screenshots; anything else -- an Xbox or GOG game, an emulator, the desktop -- gets
    /// Win+PrintScreen, which Windows saves to Pictures\Screenshots whatever has the foreground.
    /// It used to be F12 regardless, which in a browser opens the developer tools.
    /// </summary>
    private void TakeScreenshot(string source)
    {
        bool steam = (_launcher.IsGameForeground()
            && (_launcher.RunningGameId?.StartsWith("steam:", StringComparison.Ordinal) ?? false))
            || _steam.Owner() == SteamForeground.SteamGame;
        if (steam) NativeMethods.SendKeyTap(NativeMethods.VK_F12, NativeMethods.SCAN_F12);
        else ShortcutKeys.Send("Win+PrintScreen");
        Log.Info($"Screenshot: {source} → {(steam ? "F12 (Steam)" : "Win+PrintScreen")}");
    }

    public void BeginActionCapture() => _gamepad.BeginCapture();
    public void CancelActionCapture() => _gamepad.CancelCapture();

    /// <summary>
    /// Leave the game running and show the launcher properly — the "Home" action, and where
    /// "close game" lands too. Clearing the overlay flags matters: without it the host still
    /// believed a menu was up while the library was on screen, so the next combo tap dismissed a
    /// menu that wasn't there instead of minimizing.
    /// </summary>
    public void GoHome()
    {
        _overlayActive = false;
        _gamepad.MenuOwnsStick = false;
        _gamepad.ResetInputMode();
        Unpark();
    }

    private async void OnDeactivated(object? sender, EventArgs e)
    {
        if (_windowed || _suppressRefocus || _parked) return;
        if (!_settings.Settings.KeepFocus || _launcher.GameRunning)
        {
            // Nothing will take the focus back, so nothing should keep the screen either. A beat
            // first: the window being activated may not be the foreground one yet.
            await Task.Delay(50);
            if (!IsActive && !_parked && !_suppressRefocus && !_overlayActive) StepBehindForeground();
            return;
        }
        await Task.Delay(350);
        // The tray's menu takes the foreground to open. Taking it back would shut the menu before
        // anything on it could be chosen, Minimize Loungepad included; once it closes, the launcher
        // is wherever the choice left it.
        while (_tray?.MenuOpen == true) await Task.Delay(150);
        if (_suppressRefocus || _launcher.GameRunning || _parked) return;

        // Nor Steam's Big Picture, or a game Steam started: something of its own that somebody just
        // chose. Taking the foreground back would put the library over it 350 ms later.
        if (_steam.Owner() is not null) { StepBehindForeground(); return; }

        // Don't fight the virtual keyboard for focus
        var kb = NativeMethods.FindWindow("IPTip_Main_Window", null);
        var fg = NativeMethods.GetForegroundWindow();
        if (fg != IntPtr.Zero && fg == kb) return;
        if (System.Diagnostics.Process.GetProcessesByName("osk").Length > 0) return;

        Activate();
        NativeMethods.SetForegroundWindow(_hwnd);
    }

    /// <summary>
    /// The launcher was left for another window and will not take the focus back (KeepFocus off,
    /// or a game running): step behind that window. It is topmost on the TV, so with KeepFocus
    /// off Alt+Tab did activate the app that was picked -- focused, and drawn underneath
    /// Loungepad, which read as Alt+Tab doing nothing. Just below the new window, because plain
    /// NOTOPMOST puts a window above every other normal one, the one just picked included. Not
    /// behind the shell (a click on the desktop or the taskbar) or a window of ours (the tray's
    /// menu): there it only stops being topmost and stays on screen.
    /// </summary>
    private void StepBehindForeground()
    {
        var fg = NativeMethods.GetForegroundWindow();
        if (fg == _hwnd) return;
        NativeMethods.GetWindowThreadProcessId(fg, out var pid);
        if (fg != IntPtr.Zero && pid != (uint)Environment.ProcessId && !IsShellWindow(fg))
            NativeMethods.SetWindowPos(_hwnd, fg, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        // Below a normal window it has lost topmost already, and WPF's NOTOPMOST is then a no-op.
        Topmost = false;
    }

    private static bool IsShellWindow(IntPtr hwnd)
    {
        var cls = new System.Text.StringBuilder(64);
        NativeMethods.GetClassName(hwnd, cls, cls.Capacity);
        return cls.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }

    /// <summary>In front again (a click on it, the tray, the combo): back on top of the TV.</summary>
    private void BackOnTop()
    {
        if (_windowed || _parked || _suppressRefocus || Topmost) return;
        PositionOnTargetDisplay();
    }

    public void ExitApp() => Close();
}

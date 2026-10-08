using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Loungepad.Interop;
using Loungepad.Services;
using static Loungepad.Interop.NativeMethods;

namespace Loungepad;

/// <summary>
/// One thing the keyboard sends: characters, typed by code point so they do not depend on the
/// keyboard layout, or a key by virtual-key code with any modifiers held around it.
/// </summary>
public readonly record struct KeyStroke(string? Text, ushort Vk, bool Extended, ushort[] Mods)
{
    public static KeyStroke Chars(string text) => new(text, 0, false, Array.Empty<ushort>());
    public static KeyStroke Tap(ushort vk, bool extended = false, ushort[]? mods = null) =>
        new(null, vk, extended, mods ?? Array.Empty<ushort>());

    public override string ToString() => Text is { } t
        ? $"\"{t}\""
        : string.Concat(Mods.Select(m => $"0x{m:X2}+")) + $"0x{Vk:X2}";
}

/// <summary>Where the app in front has its focus and caret, to tell whether the caret moved
/// without the keyboard moving it. <see cref="CaretWindow"/> is zero when it has no system caret.</summary>
public readonly record struct TargetState(IntPtr Focus, IntPtr CaretWindow, int CaretX, int CaretY);

/// <summary>
/// A gamepad-driven on-screen keyboard that never takes the foreground.
///
/// The Windows touch keyboard is a system UIAccess window with its own focus and z-order rules,
/// which is why it misbehaves over games -- nothing outside it can fix that. This one is styled
/// WS_EX_NOACTIVATE and answers WM_MOUSEACTIVATE with MA_NOACTIVATE, so it cannot be activated
/// even by a click: whatever the user was typing into keeps focus and its caret, and keystrokes
/// injected with SendInput land there.
///
/// It cannot appear over a game in *exclusive* fullscreen -- no topmost window can, the touch
/// keyboard included. Borderless windowed is fine.
///
/// What it is made of is KeyboardLayout's business: the letters, and whichever of the function
/// row, navigation block, number pad and Ctrl/Win/Alt are switched on. Above them is a bar of word
/// suggestions (WordPredictor) with a gear at its right end, which swaps the keys for the switches
/// that decide all of that -- the same five as Settings → Keyboard -- and the keyboard's size.
/// </summary>
public partial class KeyboardWindow : Window
{
    /// <summary>
    /// Everything the keyboard types goes through here, and where the app in front has its caret
    /// comes from <see cref="Probe"/>. Both replaceable, so a harness can drive the whole keyboard
    /// and record what it would have sent without typing into whatever window is in front.
    /// </summary>
    public static Action<KeyStroke> Output = Deliver;
    public static Func<TargetState> Probe = ProbeForeground;
    /// <summary>How far Windows scales a display: 1 at 100%, 1.5 at 150%. Replaceable so a harness
    /// can ask about a display it does not have.</summary>
    public static Func<DisplayInfo, double> DisplayScale = ScaleOf;

    private KeyboardOptions _options = new(Suggestions: true, FunctionKeys: false, NavKeys: false, Numpad: false, Modifiers: false);
    private KeyboardLayout _layout = KeyboardLayout.Keys(new(true, false, false, false, false), symbols: false);
    private bool _symbols, _optionsPage, _shift;
    /// <summary>Ctrl, Win and Alt that are latched, in the order they were pressed.</summary>
    private readonly List<ushort> _mods = new();

    private Slot? _focus;
    /// <summary>The row the highlight is in. A two-row key is in both, so the key alone cannot say.</summary>
    private int _focusRow = 2;
    /// <summary>Column the highlight tries to keep while moving up and down.</summary>
    private double _wantX = 0.5;

    private double _keySize = 64, _gap = 6, _scale = 1;
    private DisplayInfo? _display;
    /// <summary>The display's <see cref="DisplayScale"/>, read when the keyboard is shown on it.</summary>
    private double _displayScale = 1;
    private TextBlock? _barNote, _sizeValue;

    // ---- suggestions ----
    private readonly WordPredictor _predictor = new();
    private readonly KeyboardText _text = new();
    /// <summary>The engine's answer, as it gave it; drawn and typed in the case the word calls for.</summary>
    private IReadOnlyList<string> _suggestions = Array.Empty<string>();
    private int _suggestSeq;

    // ---- where the caret is ----
    private readonly DispatcherTimer _settle = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private TargetState _target;
    private bool _targetSettled;

    /// <summary>Raised when the keyboard wants to close itself (B, or Menu after committing).</summary>
    public event Action? CloseRequested;

    /// <summary>A switch on the options page changed. The keyboard has already rebuilt itself; the
    /// owner keeps the settings in step.</summary>
    public event Action<KeyboardOptions>? OptionsChanged;

    /// <summary>The options page's size steps changed the size. Same contract: already applied,
    /// the owner saves it. Settings → Keyboard → Keyboard size is the same number.</summary>
    public event Action<double>? ScaleChanged;

    // The Settings slider's range; the keyboard's own steps are coarser, a tenth at a time.
    public const double ScaleMin = 0.6, ScaleMax = 1.6, ScaleStep = 0.1;

    public KeyboardWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Root.MouseMove += OnRootMouseMove;
        Root.MouseLeave += (_, _) => SetPointerOnKey(false);
        Grip.MouseEnter += (_, _) => SetPointerOnKey(false);
        Grip.MouseLeftButtonDown += (_, _) => BeginDrag();
        Grip.MouseMove += (_, _) => { if (_dragging) DragToCursor(); };
        Grip.MouseLeftButtonUp += (_, _) => EndDrag();

        // Once typing has paused, note where the caret settled. If it is somewhere else at the next
        // key, something other than the keyboard moved it.
        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            _target = Probe();
            _targetSettled = true;
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) return;
            _settle.Stop();
            _suggestSeq++;   // an answer still on its way is for a keyboard that has gone
        };
    }

    private IntPtr Handle => new WindowInteropHelper(this).Handle;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = Handle;

        // The whole point: no foreground, ever. TOOLWINDOW additionally keeps it out of Alt-Tab.
        var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));

        // WS_EX_NOACTIVATE still lets a click activate the window; this refuses that too, which is
        // what lets the pointer press keys without the app underneath losing its caret.
        HwndSource.FromHwnd(hwnd)?.AddHook(Hook);
    }

    private IntPtr Hook(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(MA_NOACTIVATE);
        }
        // Losing the capture (Alt-Tab, another window grabbing it) has to end the drag, or the
        // keyboard would follow the pointer around after the button was long since released.
        if (msg == WM_CAPTURECHANGED) _dragging = false;
        return IntPtr.Zero;
    }

    // ---- pad / pointer arming ----
    //
    // Same model as the launcher UI. The D-pad paints a highlight and A presses it; moving the
    // pointer (real mouse, or the left stick driving it) hands over to hover instead. With the
    // pointer resting on nothing there is no highlight at all, and A is released back to the
    // gamepad-mouse so it can click a text field in the app underneath -- which is the whole point
    // of a keyboard that never takes focus.

    private volatile bool _padMode = true;
    private volatile bool _pointerOnKey;
    private Point _lastPointer = new(double.NaN, double.NaN);

    /// <summary>
    /// Should the pad's buttons type? False when the pointer is driving and is not over a key,
    /// which is when they should be clicking instead. Read from the gamepad poll thread.
    /// </summary>
    public bool Armed => _padMode || _pointerOnKey;

    /// <summary>The host's input mode changed ("pad" on a D-pad press, "pointer" on stick movement).</summary>
    public void SetInputMode(string mode)
    {
        bool pad = mode == "pad";
        if (_padMode == pad) return;
        _padMode = pad;
        Paint();
    }

    private void SetPointerOnKey(bool on)
    {
        if (_pointerOnKey == on) return;
        _pointerOnKey = on;
        Paint();
    }

    /// <summary>
    /// A real mouse move is its own switch to pointer mode -- the gamepad service only sees the
    /// stick. Guarded on the position actually changing, because WPF also raises MouseMove when
    /// the tree under a stationary pointer is rebuilt.
    ///
    /// It is also what moves the highlight onto the key under the pointer. That used to be each
    /// key's MouseEnter, which has no such guard: WPF raises it for a key BUILT under a pointer
    /// that has not moved, so every rebuild -- letters to symbols, a switch on the options page --
    /// dragged the D-pad's highlight to wherever the hidden cursor happened to be parked.
    /// </summary>
    private void OnRootMouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(Root);
        if (p == _lastPointer) return;
        _lastPointer = p;
        bool changed = _padMode;
        _padMode = false;
        // Over the gaps between keys the last key stays lit, as it always has; only leaving the
        // keyboard or reaching the grab bar puts the pointer on nothing.
        if (SlotUnder(e.OriginalSource as DependencyObject) is { } s && (s != _focus || !_pointerOnKey))
        {
            FocusOn(s, s.Row);
            _pointerOnKey = true;
            changed = true;
        }
        if (changed) Paint();
    }

    private static Slot? SlotUnder(DependencyObject? d)
    {
        while (d is not null)
        {
            if (d is Border { Tag: Slot s }) return s;
            d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    // ---- moving the window ----

    private bool _dragging;
    private POINT _dragFrom;
    private int _dragOriginX, _dragOriginY;
    /// <summary>Once it has been dragged, showing it again must not yank it back to the default
    /// spot -- it was moved off something for a reason.</summary>
    private bool _moved;

    private void BeginDrag()
    {
        GetCursorPos(out _dragFrom);
        GetWindowRect(Handle, out var r);
        _dragOriginX = r.Left;
        _dragOriginY = r.Top;
        _dragging = true;
        // Capture, or the drag stops the moment the pointer outruns the grab bar.
        Grip.CaptureMouse();
    }

    private void DragToCursor()
    {
        GetCursorPos(out var p);
        _moved = true;
        MoveTo(_dragOriginX + (p.X - _dragFrom.X), _dragOriginY + (p.Y - _dragFrom.Y));
    }

    private void EndDrag()
    {
        _dragging = false;
        Grip.ReleaseMouseCapture();
    }

    /// <summary>Move without resizing or activating; screen pixels, so no DIP conversion to get wrong.</summary>
    private void MoveTo(int x, int y) =>
        SetWindowPos(Handle, HWND_TOPMOST, x, y, 0, 0, SWP_NOACTIVATE | SWP_NOSIZE);

    /// <summary>
    /// Size the keyboard for a display, show it, and park it near the bottom of that display.
    ///
    /// Order matters. WPF sizes the window to its content (SizeToContent), so the real pixel size
    /// only exists once it has been shown and laid out -- and SetWindowPos needs an HWND, which
    /// does not exist before that either. So: build, show, measure the actual window rect, then
    /// move it without resizing. Reading the rect back rather than converting DIPs by hand keeps
    /// it correct on a scaled display.
    ///
    /// Also how a change made in Settings reaches a keyboard that is already up.
    /// </summary>
    public void ShowOn(DisplayInfo display, double scale, KeyboardOptions options)
    {
        bool fresh = !IsVisible;
        double displayScale = DisplayScale(display);
        bool rescaled = Math.Abs(scale - _scale) > 0.001 || display != _display || Math.Abs(displayScale - _displayScale) > 0.001;
        _display = display;
        _displayScale = displayScale;
        _scale = scale;
        _options = options;
        if (fresh)
        {
            // A new keyboard starts on its keys, with nothing latched and nothing known about the
            // field it is typing into. A Ctrl left latched from last time would be a trap.
            _optionsPage = false;
            _shift = false;
            _mods.Clear();
            _text.Reset();
            _suggestions = Array.Empty<string>();
        }

        SizeKeys();
        Build();
        Show();
        UpdateLayout();
        FitToDisplay();
        Place(rescaled);

        if (fresh)
        {
            _target = Probe();
            _targetSettled = true;
        }
        RefreshSuggestions();
    }

    /// <summary>
    /// A key is a fixed share of the display's height, times the size setting. The share is of the
    /// display in device-independent pixels -- what every size here is measured in, and what
    /// Windows multiplies by the display's scaling -- not of its real pixels: taken from those, a
    /// display at 150% got a keyboard half as big again as the same display at 100%, and one at
    /// 300% (a 4K television's usual setting) got one that only ever stopped at FitToDisplay's
    /// limit, whatever the size was set to.
    /// </summary>
    private void SizeKeys()
    {
        if (_display is not { } d) return;
        _keySize = Math.Round(Math.Clamp(d.Height / _displayScale * 0.058 * _scale, 28, 150));
        _gap = Math.Round(Math.Max(2, _keySize * 0.10));
    }

    /// <summary>
    /// Shrink the keys until the board fits. Every block switched on adds width -- all of them
    /// make it twenty columns -- and at a large size that runs off the side of the screen.
    /// Measured in device-independent pixels against the display's size in the same, so it does
    /// not matter which monitor the window happens to be on before it is placed.
    /// </summary>
    private void FitToDisplay()
    {
        if (_display is not { } d) return;
        for (int pass = 0; pass < 3; pass++)
        {
            double w = Root.ActualWidth, h = Root.ActualHeight;
            if (w <= 0 || h <= 0) return;
            double f = Math.Min(d.Width / _displayScale * 0.96 / w, d.Height / _displayScale * 0.7 / h);
            if (f >= 1 || _keySize <= 24) return;
            _keySize = Math.Max(24, Math.Floor(_keySize * f));
            _gap = Math.Round(Math.Max(2, _keySize * 0.10));
            Build();
            UpdateLayout();
        }
    }

    /// <summary>
    /// Park it bottom-centre, or -- once it has been dragged somewhere -- leave it there but keep it
    /// on the screen: switching the number pad on grows it to the right. A new size or display
    /// invalidates wherever it was dragged to, so that re-parks it.
    /// </summary>
    private void Place(bool repark)
    {
        if (_display is not { } d) return;
        GetWindowRect(Handle, out var r);
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (_moved && !repark)
        {
            int x = Math.Clamp(r.Left, d.X, Math.Max(d.X, d.X + d.Width - w));
            int y = Math.Clamp(r.Top, d.Y, Math.Max(d.Y, d.Y + d.Height - h));
            if (x != r.Left || y != r.Top) MoveTo(x, y);
            return;
        }
        _moved = false;
        MoveTo(d.X + (d.Width - w) / 2, d.Y + d.Height - h - (int)(d.Height * 0.06));
    }

    /// <summary>The keyboard is up and its make-up changed: size, build, fit, and keep it on screen.</summary>
    private void Relayout()
    {
        SizeKeys();
        Build();
        UpdateLayout();
        FitToDisplay();
        Place(repark: false);
    }

    // ---- building the board ----

    private double Unit => _keySize + _gap;
    private double BarHeight => Math.Round(_keySize * 0.8);

    /// <summary>Top of a row. The bar is shorter than a key and stands a little apart from them.</summary>
    private double RowTop(int row) => row == 0 ? 0 : BarHeight + Math.Round(_gap * 1.6) + (row - 1) * Unit;

    private void Build()
    {
        var keep = _focus?.Key;
        _layout = _optionsPage ? KeyboardLayout.Options(_options) : KeyboardLayout.Keys(_options, _symbols);
        Board.Children.Clear();
        _barNote = null;
        _sizeValue = null;

        Grip.Height = Math.Round(_keySize * 0.44);
        Grip.Margin = new Thickness(0, 0, 0, Math.Round(_keySize * 0.10));
        GripTitle.FontSize = Math.Round(_keySize * 0.20);
        GripBar.Width = Math.Round(_keySize * 1.4);
        GripBar.Height = Math.Max(3, Math.Round(_keySize * 0.06));

        Board.Width = _layout.Width * Unit - _gap;
        Board.Height = RowTop(_layout.Rows) - _gap;

        // With no suggestion slots the bar says why: this is the options page, or suggestions are off.
        if (!_layout.Slots.Any(s => s.Key.Action == KeyAction.Suggestion))
        {
            _barNote = new TextBlock
            {
                Text = _optionsPage ? "Keyboard options" : "Word suggestions are off",
                FontSize = Math.Round(_keySize * 0.26),
                FontFamily = TextFont,
                Foreground = NoteInk,
                VerticalAlignment = VerticalAlignment.Center,
            };
            // On the options page the note stops short of the size steps.
            double noteCols = _optionsPage ? KeyboardLayout.SizeDownX(_layout.Width) : _layout.Width - 1;
            Board.Children.Add(new Border
            {
                Width = Math.Max(0, noteCols * Unit - _gap),
                Height = BarHeight,
                Padding = new Thickness(Math.Round(_keySize * 0.2), 0, 0, 0),
                Child = _barNote,
            });
        }

        // The size between its two steps: a reading, not a key, so the D-pad goes − + gear.
        if (_optionsPage)
        {
            _sizeValue = new TextBlock
            {
                FontSize = Math.Round(_keySize * 0.26),
                FontFamily = TextFont,
                Foreground = KeyInk,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var value = new Border { Width = 2 * Unit - _gap, Height = BarHeight, Child = _sizeValue };
            Canvas.SetLeft(value, Math.Round(KeyboardLayout.SizeValueX(_layout.Width) * Unit));
            Canvas.SetTop(value, RowTop(0));
            Board.Children.Add(value);
        }

        foreach (var s in _layout.Slots)
        {
            var cell = MakeCell(s, s.W * Unit - _gap, s.Row == 0 ? BarHeight : s.RowSpan * Unit - _gap);
            Canvas.SetLeft(cell, Math.Round(s.X * Unit));
            Canvas.SetTop(cell, RowTop(s.Row));
            Board.Children.Add(cell);
        }

        // Keep the highlight: on the same key if it is still there (letters and symbols share most
        // of theirs), else on whatever is now at the same spot.
        var same = keep is null ? null : _layout.Slots.FirstOrDefault(s => s.Key == keep);
        if (same is not null)
        {
            _focus = same;
            if (!same.Covers(_focusRow)) _focusRow = same.Row;
        }
        else if (_layout.Near(_focusRow, _wantX) is { } near)
        {
            _focus = near.Key;
            _focusRow = near.Row;
        }
        Paint();
    }

    private Border MakeCell(Slot s, double w, double h)
    {
        var key = s.Key;
        var content = new Grid();
        var cell = new Border
        {
            Width = w,
            Height = h,
            CornerRadius = new CornerRadius(_keySize * 0.16),
            BorderThickness = new Thickness(1),
            Child = content,
        };

        switch (key.Action)
        {
            case KeyAction.Suggestion:
                cell.CornerRadius = new CornerRadius(_keySize * 0.14);
                s.Label = new TextBlock
                {
                    FontSize = Math.Round(_keySize * 0.30),
                    FontFamily = TextFont,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(Math.Round(_keySize * 0.3), 0, Math.Round(_keySize * 0.3), 0),
                };
                content.Children.Add(s.Label);
                break;

            case KeyAction.Options:
                // Segoe Fluent Icons on Windows 11 and MDL2 Assets on 10 both have the gear at E713.
                s.Label = new TextBlock
                {
                    Text = "",
                    FontFamily = IconFont,
                    FontSize = Math.Round(_keySize * 0.34),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                content.Children.Add(s.Label);
                break;

            case KeyAction.Option:
                BuildOption(s, content);
                break;

            default:
                // One glyph -- a letter, a digit, an arrow -- gets the character size; at the
                // word-key size an arrow reads as a speck.
                bool glyph = key.Action == KeyAction.Char || key.Lower.Length == 1;
                s.Label = new TextBlock
                {
                    FontSize = glyph ? _keySize * 0.42 : _keySize * 0.26,
                    FontFamily = KeyFont,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                content.Children.Add(s.Label);
                break;
        }

        if (key.Hint is { } hint)
        {
            s.Badge = HintBadge(hint);
            content.Children.Add(s.Badge);
        }
        s.Cell = cell;
        cell.Tag = s;

        // The pointer is a first-class way to drive this: hovering moves the highlight (see
        // OnRootMouseMove) and a click presses, so the stick can type without the D-pad and vice versa.
        cell.MouseLeftButtonUp += (_, _) => { FocusOn(s, s.Row); Press(); };
        return cell;
    }

    /// <summary>An option row: its name and what it adds on the left, a switch on the right.</summary>
    private void BuildOption(Slot s, Grid content)
    {
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        double inset = Math.Round(_keySize * 0.3);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(inset, 0, 0, 0) };
        s.Label = new TextBlock
        {
            Text = s.Key.Lower,
            FontSize = Math.Round(_keySize * 0.27),
            FontFamily = TextFont,
            FontWeight = FontWeights.SemiBold,
        };
        s.Detail = new TextBlock
        {
            Text = s.Key.Detail,
            FontSize = Math.Round(_keySize * 0.19),
            FontFamily = TextFont,
            Margin = new Thickness(0, Math.Round(_keySize * 0.03), 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        text.Children.Add(s.Label);
        text.Children.Add(s.Detail);

        double th = Math.Round(_keySize * 0.40), pad = Math.Max(3, Math.Round(th * 0.14)), knob = th - 2 * pad;
        s.Knob = new Border
        {
            Width = knob,
            Height = knob,
            CornerRadius = new CornerRadius(knob / 2),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(pad - 1.5, 0, pad - 1.5, 0),
        };
        s.Track = new Border
        {
            Width = Math.Round(th * 1.8),
            Height = th,
            CornerRadius = new CornerRadius(th / 2),
            BorderThickness = new Thickness(1.5),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(inset, 0, inset, 0),
            Child = s.Knob,
        };
        Grid.SetColumn(s.Track, 1);
        content.Children.Add(text);
        content.Children.Add(s.Track);
    }

    private string Face(KeyDef k) =>
        k.Action == KeyAction.Char && _shift && k.Upper is { } up ? up : k.Lower;

    // Brighter than the first pass: on a dark key over a dark game the old fill and the background
    // were nearly the same value, so the grid read as one grey slab. Each key now carries its own
    // lighter fill and a visible edge.
    private static readonly FontFamily KeyFont = new("Segoe UI Symbol, Segoe UI");
    private static readonly FontFamily TextFont = new("Segoe UI");
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private static readonly Brush KeyFill = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x48));
    private static readonly Brush KeyEdge = new SolidColorBrush(Color.FromRgb(0x7E, 0x7E, 0x92));
    private static readonly Brush KeyInk = new SolidColorBrush(Colors.White);
    private static readonly Brush FocusFill = new SolidColorBrush(Color.FromRgb(0xF0, 0xA2, 0x53));
    private static readonly Brush FocusEdge = new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x8E));
    private static readonly Brush FocusInk = new SolidColorBrush(Color.FromRgb(0x08, 0x08, 0x0A));
    private static readonly Brush LatchFill = new SolidColorBrush(Color.FromRgb(0x7A, 0x59, 0x36));
    private static readonly Brush PillFill = new SolidColorBrush(Color.FromArgb(0xB8, 0x14, 0x14, 0x18));
    private static readonly Brush PillInk = new SolidColorBrush(Color.FromRgb(0xF3, 0xF2, 0xF0));
    // The bar sits between the window and the keys in value, so it reads as a strip rather than
    // another row of keys -- which is what it is.
    private static readonly Brush BarFill = new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x2E));
    private static readonly Brush NoteInk = new SolidColorBrush(Color.FromArgb(0x8A, 0xF6, 0xF5, 0xF3));
    private static readonly Brush DetailInk = new SolidColorBrush(Color.FromRgb(0xB4, 0xB4, 0xC2));
    private static readonly Brush FocusDetail = new SolidColorBrush(Color.FromRgb(0x3A, 0x2A, 0x18));
    private static readonly Brush TrackOff = new SolidColorBrush(Color.FromRgb(0x24, 0x24, 0x2B));
    private static readonly Brush KnobOff = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA8));

    /// <summary>
    /// The pad button drawn in a key's corner. The face buttons get their real shape and colour
    /// and the shoulders/sticks a pill, because "A" as a green disc is read at a glance from the
    /// sofa where the word "Menu" in 11px is not. Which shape depends on the pad in hand: the
    /// hints are bound by position (see KeyboardButtons in GamepadService), so the badge on
    /// Backspace is a blue X on an Xbox pad and a pink square on a DualSense.
    /// </summary>
    private string _padLayout = "xbox";

    /// <summary>The family of the pad in use; the badges are redrawn if the keyboard is up.</summary>
    public void SetLayout(string layout)
    {
        if (string.IsNullOrEmpty(layout) || _padLayout == layout) return;
        _padLayout = layout;
        if (IsVisible) Build();
    }

    private static readonly Color DarkDisc = Color.FromRgb(0x2B, 0x2B, 0x31);
    private static readonly Color PillInkColor = Color.FromRgb(0xF3, 0xF2, 0xF0);
    private static readonly Color DarkInk = Color.FromRgb(0x08, 0x08, 0x0A);

    /// <summary>What a hint looks like on the pad in use: its text, the disc colour for a face button (null for a pill), and the ink.</summary>
    private (string text, Color? disc, Color ink) HintFace(string hint)
    {
        var white = Colors.White;
        switch (_padLayout)
        {
            case "playstation":
                return hint switch
                {
                    "A" => ("✕", DarkDisc, Color.FromRgb(0x7C, 0x9B, 0xE6)),
                    "B" => ("○", DarkDisc, Color.FromRgb(0xE0, 0x55, 0x4F)),
                    "X" => ("□", DarkDisc, Color.FromRgb(0xE6, 0x8A, 0xC0)),
                    "Y" => ("△", DarkDisc, Color.FromRgb(0x63, 0xC5, 0x8F)),
                    "LB" => ("L1", null, PillInkColor), "RB" => ("R1", null, PillInkColor),
                    "LT" => ("L2", null, PillInkColor), "RT" => ("R2", null, PillInkColor),
                    "LS" => ("L3", null, PillInkColor), "RS" => ("R3", null, PillInkColor),
                    "Menu" => ("☰", null, PillInkColor), "View" => ("❐", null, PillInkColor),
                    _ => (hint, null, PillInkColor),
                };
            case "switch":
                // By position: Nintendo's B is the bottom button, which is what "A" means here.
                return hint switch
                {
                    "A" => ("B", Color.FromRgb(0x1B, 0x1B, 0x1F), white),
                    "B" => ("A", Color.FromRgb(0x1B, 0x1B, 0x1F), white),
                    "X" => ("Y", Color.FromRgb(0x1B, 0x1B, 0x1F), white),
                    "Y" => ("X", Color.FromRgb(0x1B, 0x1B, 0x1F), white),
                    "LB" => ("L", null, PillInkColor), "RB" => ("R", null, PillInkColor),
                    "LT" => ("ZL", null, PillInkColor), "RT" => ("ZR", null, PillInkColor),
                    "Menu" => ("+", null, PillInkColor), "View" => ("−", null, PillInkColor),
                    _ => (hint, null, PillInkColor),
                };
            case "generic":
                // No letters to show for a pad we know nothing about: the position is the name.
                return hint switch
                {
                    "A" => ("▼", DarkDisc, white), "B" => ("▶", DarkDisc, white),
                    "X" => ("◀", DarkDisc, white), "Y" => ("▲", DarkDisc, white),
                    "LB" => ("L1", null, PillInkColor), "RB" => ("R1", null, PillInkColor),
                    "LT" => ("L2", null, PillInkColor), "RT" => ("R2", null, PillInkColor),
                    "LS" => ("L3", null, PillInkColor), "RS" => ("R3", null, PillInkColor),
                    "Menu" => ("START", null, PillInkColor), "View" => ("SEL", null, PillInkColor),
                    _ => (hint, null, PillInkColor),
                };
            default:
                return hint switch
                {
                    "A" => ("A", Color.FromRgb(0x3A, 0xA0, 0x3C), white),
                    "B" => ("B", Color.FromRgb(0xD3, 0x43, 0x3C), white),
                    "X" => ("X", Color.FromRgb(0x3C, 0x7C, 0xD3), white),
                    // Yellow needs dark ink; the other three carry white.
                    "Y" => ("Y", Color.FromRgb(0xE2, 0xB1, 0x28), DarkInk),
                    // Menu (three bars) and View are glyphs on the pad itself, so draw them, not their names.
                    "Menu" => ("☰", null, PillInkColor), "View" => ("❐", null, PillInkColor),
                    _ => (hint, null, PillInkColor),
                };
        }
    }

    private FrameworkElement HintBadge(string hint)
    {
        var (text, disc, ink) = HintFace(hint);
        bool face = disc is not null;
        double h = Math.Round(_keySize * 0.28);
        // Inset past the key's own corner radius, or the badge rides out over the rounded edge.
        double inset = Math.Round(_keySize * 0.10);
        bool glyph = text.Length == 1 && !char.IsLetterOrDigit(text[0]);

        var badge = new Border
        {
            Height = h,
            MinWidth = h,
            Background = face ? new SolidColorBrush(disc!.Value) : PillFill,
            CornerRadius = new CornerRadius(h / 2),
            BorderThickness = new Thickness(face ? 0 : 1),
            BorderBrush = face ? Brushes.Transparent : new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
            Padding = new Thickness(face ? 0 : _keySize * 0.07, 0, face ? 0 : _keySize * 0.07, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, inset, inset, 0),
            Child = new TextBlock
            {
                Text = text,
                FontSize = Math.Round(_keySize * (glyph ? 0.16 : 0.17)),
                FontFamily = KeyFont,
                FontWeight = face ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = new SolidColorBrush(ink),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        return badge;
    }

    /// <summary>Repaint faces, suggestions, switches and the highlight without rebuilding the tree.</summary>
    private void Paint()
    {
        bool show = Armed;
        if (_sizeValue is { } sv) sv.Text = $"Size {Math.Round(_scale * 100)}%";
        foreach (var s in _layout.Slots)
        {
            if (s.Cell is not { } cell) continue;
            var key = s.Key;
            bool focused = show && s == _focus;

            switch (key.Action)
            {
                case KeyAction.Suggestion:
                {
                    string text = key.Index < _suggestions.Count ? _text.Display(_suggestions[key.Index], _shift) : "";
                    cell.Background = focused ? FocusFill : BarFill;
                    cell.BorderBrush = focused ? FocusEdge : Brushes.Transparent;
                    s.Label!.Text = text;
                    s.Label.Foreground = focused ? FocusInk : KeyInk;
                    // RS takes the first suggestion; the badge only says so when there is one.
                    if (s.Badge is { } b) b.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                    continue;
                }
                case KeyAction.Option:
                {
                    // On is a filled track with the knob right; off is a hollow one with it left.
                    // Over the orange of the highlight the fill flips to dark, so both still read.
                    bool on = _options[key.Option];
                    cell.Background = focused ? FocusFill : KeyFill;
                    cell.BorderBrush = focused ? FocusEdge : KeyEdge;
                    s.Label!.Foreground = focused ? FocusInk : KeyInk;
                    s.Detail!.Foreground = focused ? FocusDetail : DetailInk;
                    s.Track!.Background = on ? (focused ? FocusInk : FocusFill) : (focused ? Brushes.Transparent : TrackOff);
                    s.Track.BorderBrush = on ? (focused ? FocusInk : FocusFill) : (focused ? FocusInk : KeyEdge);
                    s.Knob!.Background = on ? (focused ? FocusFill : KeyInk) : (focused ? FocusInk : KnobOff);
                    s.Knob.HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left;
                    continue;
                }
            }

            bool latched = key.Action switch
            {
                KeyAction.Shift => _shift,
                KeyAction.Modifier => _mods.Contains(key.Vk),
                KeyAction.Options => _optionsPage,
                _ => false,
            };
            cell.Background = focused ? FocusFill : latched ? LatchFill : KeyFill;
            cell.BorderBrush = focused ? FocusEdge : KeyEdge;
            // A size step that has reached its end is drawn spent; pressing it does nothing.
            bool spent = (key.Action == KeyAction.SizeDown && _scale <= ScaleMin + 0.001)
                      || (key.Action == KeyAction.SizeUp && _scale >= ScaleMax - 0.001);
            if (s.Label is { } tb)
            {
                if (key.Action != KeyAction.Options) tb.Text = Face(key);
                tb.Foreground = focused ? FocusInk : spent ? NoteInk : KeyInk;
            }
        }
    }

    // ---- gamepad-facing API ----

    private void FocusOn(Slot s, int row)
    {
        _focus = s;
        _focusRow = row;
        _wantX = s.Center;
    }

    public void Move(string dir)
    {
        // A D-pad press is the pad taking over from the pointer, exactly as in the launcher.
        _padMode = true;
        if (_focus is not null)
        {
            switch (dir)
            {
                case "Left":
                case "Right":
                    FocusOn(_layout.Step(_focus, _focusRow, dir == "Left" ? -1 : 1), _focusRow);
                    break;
                case "Up":
                case "Down":
                    // _wantX is sticky, so passing through a wide key and out the other side
                    // returns to the column you started in rather than that key's middle.
                    if (_layout.Vertical(_focus, _focusRow, _wantX, dir == "Up" ? -1 : 1) is { } to)
                    {
                        _focus = to.Key;
                        _focusRow = to.Row;
                    }
                    break;
            }
        }
        Paint();
    }

    /// <summary>Press the highlighted key.</summary>
    public void Press()
    {
        if (!Armed || _focus is null) return;
        var key = _focus.Key;
        switch (key.Action)
        {
            case KeyAction.Options: ToggleOptionsPage(); return;
            case KeyAction.Option:  SetOption(key.Option, !_options[key.Option]); return;
            case KeyAction.SizeDown: StepScale(-1); return;
            case KeyAction.SizeUp:   StepScale(1); return;
        }
        if (_optionsPage) return;

        switch (key.Action)
        {
            case KeyAction.Char:       TypeChar(Face(key)[0]); break;
            case KeyAction.Shift:      ToggleShift(); break;
            case KeyAction.Layer:      ToggleLayer(); break;
            case KeyAction.Backspace:  Backspace(); break;
            // Delete only takes what is after the caret, so the word being typed is untouched.
            case KeyAction.Delete:     TapKey(VK_DELETE, extended: true, takesShift: false, keepsWord: true); break;
            case KeyAction.Space:      Space(); break;
            case KeyAction.Enter:      Commit(); break;
            case KeyAction.Tab:        TapKey(VK_TAB, extended: false, takesShift: true); break;
            case KeyAction.Escape:     TapKey(VK_ESCAPE, extended: false, takesShift: false); break;
            case KeyAction.CaretLeft:  CaretLeft(); break;
            case KeyAction.CaretRight: CaretRight(); break;
            case KeyAction.Vk:         TapKey(key.Vk, key.Extended, key.TakesShift); break;
            case KeyAction.Modifier:   ToggleModifier(key.Vk); break;
            case KeyAction.Suggestion: AcceptSuggestion(key.Index); break;
        }
    }

    /// <summary>
    /// A character key. With Ctrl, Win or Alt latched it is a shortcut, and Ctrl then C has to
    /// arrive as Ctrl+C: a character sent by code point with Ctrl held is not one to most apps. So
    /// it goes as the key that types that character on the current layout, with the shift state the
    /// layout needs for it -- Ctrl, Shift, Z for a latched Shift's "Z".
    /// </summary>
    private void TypeChar(char c)
    {
        CheckTarget();
        if (_mods.Count > 0)
        {
            short scan = VkKeyScanW(c);
            if (scan != -1)
            {
                var mods = new List<ushort>(_mods);
                int state = (scan >> 8) & 0xFF;
                if ((state & 1) != 0 && !mods.Contains(VK_SHIFT)) mods.Add(VK_SHIFT);
                if ((state & 2) != 0 && !mods.Contains(VK_CONTROL)) mods.Add(VK_CONTROL);
                if ((state & 4) != 0 && !mods.Contains(VK_MENU)) mods.Add(VK_MENU);
                Output(KeyStroke.Tap((ushort)(scan & 0xFF), extended: false, mods.ToArray()));
            }
            else Output(KeyStroke.Chars(c.ToString()));
            _mods.Clear();
            _shift = false;
            // A shortcut can do anything to the text -- select all, paste, undo.
            _text.Reset();
            Typed();
            return;
        }

        if (_text.PunctuationSwap(c) is { } swapped)
        {
            Output(KeyStroke.Tap(VK_BACK));
            Output(KeyStroke.Chars(swapped));
            _text.Punctuated(swapped);
        }
        else
        {
            Output(KeyStroke.Chars(c.ToString()));
            _text.Typed(c.ToString());
        }
        // Shift is one-shot, like a phone keyboard: nobody wants to unlatch it by hand after
        // every capital.
        _shift = false;
        Typed();
    }

    /// <summary>
    /// Tap a key with whatever is latched, then let the latches it used go. A latched Shift only
    /// goes with keys that take it (Tab, the arrows, F-keys); Ctrl, Win and Alt go with anything.
    /// Everything but Delete loses track of the word: the caret has moved, or might have.
    /// </summary>
    private void TapKey(ushort vk, bool extended, bool takesShift, bool keepsWord = false)
    {
        bool shift = takesShift && _shift;
        bool combo = _mods.Count > 0;
        var mods = new List<ushort>(_mods);
        if (shift) mods.Add(VK_SHIFT);
        CheckTarget();
        Output(KeyStroke.Tap(vk, extended, mods.ToArray()));
        if (shift) _shift = false;
        _mods.Clear();
        if (!keepsWord || combo) _text.Reset();
        Typed();
    }

    /// <summary>After anything went to the app: re-arm the caret check, ask for suggestions, repaint.</summary>
    private void Typed()
    {
        _settle.Stop();
        _settle.Start();
        RefreshSuggestions();
        Paint();
    }

    public void Backspace()
    {
        if (_optionsPage) return;
        CheckTarget();
        bool combo = _mods.Count > 0;
        // Ctrl+Backspace takes a whole word, which is worth having; it also means the tail no
        // longer knows what is there.
        Output(KeyStroke.Tap(VK_BACK, extended: false, _mods.ToArray()));
        if (combo) { _mods.Clear(); _text.Reset(); }
        else _text.Backspaced();
        Typed();
    }

    public void Space()
    {
        if (_optionsPage) return;
        if (_mods.Count > 0) { TapKey(VK_SPACE, extended: false, takesShift: false); return; }
        CheckTarget();
        Output(KeyStroke.Chars(" "));
        _text.Typed(" ");
        Typed();
    }

    public void ToggleShift()
    {
        if (_optionsPage) return;
        _shift = !_shift;
        // The suggestions follow: a latched Shift capitalises the one you pick.
        Paint();
    }

    public void ToggleLayer()
    {
        if (_optionsPage) return;
        _symbols = !_symbols;
        _shift = false;
        Build();
    }

    private void ToggleModifier(ushort vk)
    {
        if (!_mods.Remove(vk)) _mods.Add(vk);
        Paint();
    }

    public void CaretLeft()
    {
        if (_optionsPage) return;
        TapKey(VK_LEFT, extended: true, takesShift: true);
    }

    public void CaretRight()
    {
        if (_optionsPage) return;
        TapKey(VK_RIGHT, extended: true, takesShift: true);
    }

    /// <summary>Enter, then get out of the way -- the same thing Menu does on the Xbox keyboard.
    /// On the options page it only goes back to the keys.</summary>
    public void Commit()
    {
        if (_optionsPage) { ToggleOptionsPage(); return; }
        CheckTarget();
        Output(KeyStroke.Tap(VK_RETURN, extended: false, _mods.ToArray()));
        _mods.Clear();
        _text.Reset();
        CloseRequested?.Invoke();
    }

    public void RequestClose() => CloseRequested?.Invoke();

    // ---- suggestions ----

    /// <summary>
    /// Put a suggestion in place of the word being typed, with a space after it. RS takes the
    /// first; A takes the highlighted one. Only the letters that differ are retyped.
    /// </summary>
    public void AcceptSuggestion(int index)
    {
        if (_optionsPage || index >= _suggestions.Count) return;
        var candidate = _suggestions[index];
        CheckTarget();
        var (backspaces, insert) = _text.Replacement(candidate, _shift);
        for (int i = 0; i < backspaces; i++) Output(KeyStroke.Tap(VK_BACK));
        Output(KeyStroke.Chars(insert));
        _text.Replaced(backspaces, insert);
        _shift = false;
        Typed();
    }

    /// <summary>
    /// Ask for suggestions for the word as it now stands. The answer lands a few milliseconds later
    /// and is dropped if anything was typed in between. The old ones stay up until then, so the bar
    /// does not blink on every key.
    /// </summary>
    private void RefreshSuggestions()
    {
        int seq = ++_suggestSeq;
        var word = _text.CurrentWord;
        // A word with a digit in it is a code, a time or a gamertag -- nothing to finish.
        if (!_options.Suggestions || _optionsPage || word.Any(char.IsDigit))
        {
            if (_suggestions.Count > 0) { _suggestions = Array.Empty<string>(); Paint(); }
            return;
        }
        var previous = _text.PreviousWords(3);
        int max = KeyboardLayout.SuggestionCount(KeyboardLayout.WidthFor(_options));
        Task.Run(() => _predictor.SuggestAsync(word, previous, max)).ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully) return;
            Dispatcher.BeginInvoke(() =>
            {
                if (seq != _suggestSeq) return;
                _suggestions = t.Result;
                Paint();
            });
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// Before anything goes to the app: has its caret moved since the keyboard last looked? A
    /// different focused window or control, or a caret that has left the spot it settled on after
    /// the last key, means somebody clicked or used a real keyboard -- and the word the keyboard
    /// thinks it is in is no longer under the caret. Replacing it then would delete whatever is.
    /// Apps with no system caret (most games) are judged on focus alone.
    /// </summary>
    private void CheckTarget()
    {
        var now = Probe();
        bool moved = now.Focus != _target.Focus
            || (_targetSettled && now.CaretWindow != IntPtr.Zero
                && (now.CaretWindow != _target.CaretWindow || now.CaretX != _target.CaretX || now.CaretY != _target.CaretY));
        if (moved) _text.Reset();
        _target = now;
        _targetSettled = false;
    }

    /// <summary>The pad clicked somewhere while the keyboard was up: the caret may be anywhere now.</summary>
    public void TargetClicked()
    {
        _text.Reset();
        RefreshSuggestions();
        Paint();
    }

    private static TargetState ProbeForeground()
    {
        var fg = GetForegroundWindow();
        uint thread = GetWindowThreadProcessId(fg, out _);
        var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        if (thread == 0 || !GetGUIThreadInfo(thread, ref info)) return new TargetState(fg, IntPtr.Zero, 0, 0);
        return new TargetState(info.hwndFocus != IntPtr.Zero ? info.hwndFocus : fg,
            info.hwndCaret, info.rcCaret.Left, info.rcCaret.Top);
    }

    /// <summary>The scaling of the monitor under the middle of the display: 1 if it cannot be read.</summary>
    private static double ScaleOf(DisplayInfo d)
    {
        try
        {
            var monitor = MonitorFromPoint(new POINT { X = d.X + d.Width / 2, Y = d.Y + d.Height / 2 }, MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero && GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var dpi, out _) == 0 && dpi > 0)
                return dpi / 96.0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        return 1;
    }

    private static void Deliver(KeyStroke s)
    {
        if (s.Text is { } text) SendText(text);
        else SendKeyCombo(s.Vk, s.Extended, s.Mods);
    }

    // ---- the options page ----

    /// <summary>
    /// The gear swaps the keys for the options and back. Opening lands on the first switch;
    /// closing lands back on the gear, so A, A is in and out again.
    /// </summary>
    private void ToggleOptionsPage()
    {
        _optionsPage = !_optionsPage;
        _focus = null;
        RefreshSuggestions();
        Build();
        var land = _layout.Slots.First(s => s.Key.Action == (_optionsPage ? KeyAction.Option : KeyAction.Options));
        FocusOn(land, land.Row);
        Paint();
    }

    /// <summary>B on the options page goes back to the keys rather than closing the keyboard.</summary>
    public bool LeaveOptions()
    {
        if (!_optionsPage) return false;
        ToggleOptionsPage();
        return true;
    }

    private void SetOption(KeyboardOption option, bool on)
    {
        _options = _options.With(option, on);
        OptionsChanged?.Invoke(_options);
        Relayout();
    }

    /// <summary>
    /// A tenth bigger or smaller, from the keyboard itself, so trying a size does not mean leaving
    /// it for Settings. The board is rebuilt at the new size and stays where it was (or re-parks at
    /// the bottom if it was never dragged); the highlight stays on the step, since the keys are
    /// records and the rebuilt one equals the old. FitToDisplay may still cap a size the screen
    /// cannot take, as it does for the slider.
    /// </summary>
    private void StepScale(int dir)
    {
        var next = Math.Round(Math.Clamp(_scale + dir * ScaleStep, ScaleMin, ScaleMax), 2);
        if (Math.Abs(next - _scale) < 0.001) return;
        _scale = next;
        ScaleChanged?.Invoke(_scale);
        Relayout();
    }
}

using System.Windows;
using System.Windows.Controls;
using Loungepad.Models;
using static Loungepad.Interop.NativeMethods;

namespace Loungepad;

internal enum KeyAction
{
    Char, Shift, Layer, Backspace, Delete, Space, Enter, Tab, Escape, CaretLeft, CaretRight,
    /// <summary>Any other key, sent by virtual-key code: F1-F12, the navigation block, the number pad.</summary>
    Vk,
    /// <summary>Ctrl, Win or Alt. Latches for the next key, like Shift.</summary>
    Modifier,
    /// <summary>A slot in the suggestion bar; <see cref="KeyDef.Index"/> says which.</summary>
    Suggestion,
    /// <summary>The gear at the right end of the suggestion bar.</summary>
    Options,
    /// <summary>A switch on the options page; <see cref="KeyDef.Option"/> says which.</summary>
    Option,
    /// <summary>The options page's size steps, in its bar beside the gear.</summary>
    SizeDown, SizeUp,
}

public enum KeyboardOption { Suggestions, FunctionKeys, NavKeys, Numpad, Modifiers }

/// <summary>The switches that decide what the keyboard is made of. Kept in AppSettings.</summary>
public readonly record struct KeyboardOptions(bool Suggestions, bool FunctionKeys, bool NavKeys, bool Numpad, bool Modifiers)
{
    public static KeyboardOptions From(AppSettings s) =>
        new(s.KeyboardSuggestions, s.KeyboardFunctionKeys, s.KeyboardNavKeys, s.KeyboardNumpad, s.KeyboardModifiers);

    public void WriteTo(AppSettings s)
    {
        s.KeyboardSuggestions = Suggestions;
        s.KeyboardFunctionKeys = FunctionKeys;
        s.KeyboardNavKeys = NavKeys;
        s.KeyboardNumpad = Numpad;
        s.KeyboardModifiers = Modifiers;
    }

    public bool this[KeyboardOption o] => o switch
    {
        KeyboardOption.Suggestions => Suggestions,
        KeyboardOption.FunctionKeys => FunctionKeys,
        KeyboardOption.NavKeys => NavKeys,
        KeyboardOption.Numpad => Numpad,
        _ => Modifiers,
    };

    public KeyboardOptions With(KeyboardOption o, bool on) => o switch
    {
        KeyboardOption.Suggestions => this with { Suggestions = on },
        KeyboardOption.FunctionKeys => this with { FunctionKeys = on },
        KeyboardOption.NavKeys => this with { NavKeys = on },
        KeyboardOption.Numpad => this with { Numpad = on },
        _ => this with { Modifiers = on },
    };
}

/// <summary>
/// One key. <paramref name="Units"/> is its width in columns, and <paramref name="Hint"/> is the
/// gamepad button drawn in its corner, so the shortcuts are discoverable without a manual.
/// </summary>
internal sealed record KeyDef(string Lower, string? Upper = null, KeyAction Action = KeyAction.Char,
                              double Units = 1, string? Hint = null)
{
    public ushort Vk { get; init; }
    public bool Extended { get; init; }
    /// <summary>A latched Shift goes with this key: Shift+Tab, Shift+an arrow to select. Characters
    /// take Shift through their face instead, and Backspace, Space and Enter never do.</summary>
    public bool TakesShift { get; init; }
    /// <summary>Rows the key stands across -- the number pad's + and Enter are two tall.</summary>
    public int RowSpan { get; init; } = 1;
    public int Index { get; init; }
    public KeyboardOption Option { get; init; }
    /// <summary>The second line under an option's name.</summary>
    public string? Detail { get; init; }
}

/// <summary>
/// A key at its place on the board. Positions are in columns, where a column is one key and the
/// gap after it; blocks sit half a column apart. Navigation works on these numbers, so the key
/// above "s" is whichever key is over the middle of "s" -- in any block, across any gap.
/// </summary>
internal sealed class Slot
{
    public Slot(KeyDef key, int row, double x) { Key = key; Row = row; X = x; }

    public KeyDef Key { get; }
    public int Row { get; }
    public double X { get; }
    public double W => Key.Units;
    public int RowSpan => Key.RowSpan;
    public double Center => X + W / 2;
    public bool Covers(int row) => row >= Row && row < Row + RowSpan;
    public double DistanceTo(double x) => x < X ? X - x : x > X + W ? x - (X + W) : 0;

    // What the window built for it; repainted in place.
    public Border? Cell;
    public TextBlock? Label, Detail;
    public FrameworkElement? Badge;
    public Border? Track, Knob;
}

/// <summary>
/// The keys for a set of options. Row 0 is always the suggestion bar; the keys start on row 1.
///
/// The main block keeps the old 12-column skeleton exactly -- same right-hand column, same bottom
/// row -- so switching letters and symbols still only changes faces. Everything optional is laid
/// against it: F1-F12 above it (12 keys, one per column), the navigation block and the number pad
/// to its right, Ctrl/Win/Alt taking room from Space on its bottom row.
/// </summary>
internal sealed class KeyboardLayout
{
    public const double MainWidth = 12;
    public const double BlockGap = 0.5;

    public List<Slot> Slots { get; } = new();
    /// <summary>Board width in columns.</summary>
    public double Width { get; private init; }
    /// <summary>Rows under the bar. The options page takes the keys' count, so opening it never
    /// changes the keyboard's height.</summary>
    public int BodyRows { get; private init; }

    public static double WidthFor(KeyboardOptions o) =>
        MainWidth + (o.NavKeys ? BlockGap + 3 : 0) + (o.Numpad ? BlockGap + 4 : 0);

    public static int BodyRowsFor(KeyboardOptions o) => o.FunctionKeys ? 6 : 5;

    /// <summary>Three suggestions on the plain keyboard, up to five across a full one: a slot
    /// narrower than about three and a half keys cuts off ordinary words.</summary>
    public static int SuggestionCount(double width) => Math.Clamp((int)((width - 1) / 3.6), 3, 5);

    // ---- the keys ----

    private static KeyDef[] Chars(string chars) =>
        chars.Select(c => new KeyDef(c.ToString(), char.ToUpperInvariant(c).ToString())).ToArray();

    private static KeyDef Key(string label, ushort vk, bool ext = false, bool shift = false, double units = 1) =>
        new(label, Action: KeyAction.Vk, Units: units) { Vk = vk, Extended = ext, TakesShift = shift };

    // Hints match the Xbox keyboard's own bindings, so muscle memory carries over.
    private static readonly KeyDef BackKey = new("Back", Action: KeyAction.Backspace, Units: 2, Hint: "X");
    private static readonly KeyDef ShiftKey = new("Shift", Action: KeyAction.Shift, Units: 2, Hint: "LS");
    private static readonly KeyDef EnterKey = new("Enter", Action: KeyAction.Enter, Units: 2, Hint: "Menu");
    private static readonly KeyDef DelKey = new("Del", Action: KeyAction.Delete, Units: 2);
    private static readonly KeyDef LeftKey = new("←", Action: KeyAction.CaretLeft, Hint: "LB") { TakesShift = true };
    private static readonly KeyDef RightKey = new("→", Action: KeyAction.CaretRight, Hint: "RB") { TakesShift = true };
    private static readonly KeyDef LettersKey = new("&123", Action: KeyAction.Layer, Units: 2, Hint: "LT");
    private static readonly KeyDef SymbolsKey = new("abc", Action: KeyAction.Layer, Units: 2, Hint: "LT");

    public static readonly KeyDef Gear = new("", Action: KeyAction.Options);
    public static readonly KeyDef SizeDown = new("−", Action: KeyAction.SizeDown);
    public static readonly KeyDef SizeUp = new("+", Action: KeyAction.SizeUp);

    /// <summary>Where the size steps sit in the options page's bar, in columns: −, then two columns
    /// for the value (drawn by the window, not a key), then +, then the gear in the last column.</summary>
    public static double SizeDownX(double width) => width - 5;
    public static double SizeValueX(double width) => width - 4;
    public static double SizeUpX(double width) => width - 2;

    /// <summary>
    /// The bottom row. With Ctrl/Win/Alt on, they go where a real keyboard has them -- left of
    /// Space, in that order -- and Space, Tab and Esc give up the room.
    /// </summary>
    private static KeyDef[] BottomRow(KeyDef layer, bool modifiers) => modifiers
        ? new[]
        {
            layer,
            new KeyDef("Ctrl", Action: KeyAction.Modifier) { Vk = VK_CONTROL },
            new KeyDef("Win", Action: KeyAction.Modifier) { Vk = VK_LWIN },
            new KeyDef("Alt", Action: KeyAction.Modifier) { Vk = VK_MENU },
            new KeyDef("Space", Action: KeyAction.Space, Units: 5, Hint: "Y"),
            new KeyDef("Tab", Action: KeyAction.Tab) { TakesShift = true },
            new KeyDef("Esc", Action: KeyAction.Escape),
        }
        : new[]
        {
            layer,
            new KeyDef("Tab", Action: KeyAction.Tab, Units: 2) { TakesShift = true },
            new KeyDef("Space", Action: KeyAction.Space, Units: 6, Hint: "Y"),
            new KeyDef("Esc", Action: KeyAction.Escape, Units: 2),
        };

    private static KeyDef[][] Letters(bool modifiers) => new[]
    {
        Chars("1234567890").Append(LeftKey).Append(RightKey).ToArray(),
        Chars("qwertyuiop").Append(BackKey).ToArray(),
        Chars("asdfghjkl;").Append(EnterKey).ToArray(),
        Chars("zxcvbnm,./").Append(ShiftKey).ToArray(),
        BottomRow(LettersKey, modifiers),
    };

    // The apostrophe, double quote and backtick go in by code point (39, 34, 96) so the last row
    // does not turn into a thicket of escapes.
    //
    // Shift has nothing to do here -- the layer already carries both cases of every ASCII symbol,
    // so a Shift key would light up and type nothing -- and the slot goes to Del instead.
    private static KeyDef[][] Symbols(bool modifiers) => new[]
    {
        Chars("1234567890").Append(LeftKey).Append(RightKey).ToArray(),
        Chars("!@#$%^&*()").Append(BackKey).ToArray(),
        Chars("-_=+[]{}\\|").Append(EnterKey).ToArray(),
        Chars(";:" + (char)39 + (char)34 + (char)96 + "~<>?/").Append(DelKey).ToArray(),
        BottomRow(SymbolsKey, modifiers),
    };

    private void Add(int row, double x, params KeyDef[] keys)
    {
        foreach (var k in keys)
        {
            Slots.Add(new Slot(k, row, x));
            x += k.Units;
        }
    }

    public static KeyboardLayout Keys(KeyboardOptions o, bool symbols)
    {
        var l = new KeyboardLayout { Width = WidthFor(o), BodyRows = BodyRowsFor(o) };
        l.AddBar(o.Suggestions);

        double navX = MainWidth + BlockGap;
        int top = 1;
        if (o.FunctionKeys)
        {
            l.Add(1, 0, Enumerable.Range(0, 12).Select(i => Key($"F{i + 1}", (ushort)(VK_F1 + i), shift: true)).ToArray());
            // Over the navigation block, as on a real keyboard. Nowhere to go without it.
            if (o.NavKeys)
                l.Add(1, navX, Key("PrtSc", VK_SNAPSHOT, ext: true), Key("ScrLk", VK_SCROLL), Key("Pause", VK_PAUSE));
            top = 2;
        }

        var main = symbols ? Symbols(o.Modifiers) : Letters(o.Modifiers);
        for (int r = 0; r < main.Length; r++) l.Add(top + r, 0, main[r]);

        double x = MainWidth;
        if (o.NavKeys)
        {
            x += BlockGap;
            // Every key here is extended (E0-prefixed): without the flag, Home is the number pad's
            // 7 and the arrows are its 8, 4, 2 and 6.
            l.Add(top, x, Key("Ins", VK_INSERT, true, true), Key("Home", VK_HOME, true, true), Key("PgUp", VK_PRIOR, true, true));
            l.Add(top + 1, x, Key("Del", VK_DELETE, true, true), Key("End", VK_END, true, true), Key("PgDn", VK_NEXT, true, true));
            // The row between is empty, exactly as on a keyboard; navigation steps over the gap.
            l.Add(top + 3, x + 1, Key("↑", VK_UP, true, true));
            l.Add(top + 4, x, Key("←", VK_LEFT, true, true), Key("↓", VK_DOWN, true, true), Key("→", VK_RIGHT, true, true));
            x += 3;
        }
        if (o.Numpad)
        {
            x += BlockGap;
            // Real number-pad keys, not digits: a game that binds Num 8 has to see Num 8. Windows
            // turns VK_NUMPAD7 into "7" whatever Num Lock says, so the digits always type digits.
            ushort N(int d) => (ushort)(VK_NUMPAD0 + d);
            l.Add(top, x, Key("Num", VK_NUMLOCK, ext: true), Key("/", VK_DIVIDE, ext: true), Key("*", VK_MULTIPLY), Key("−", VK_SUBTRACT));
            l.Add(top + 1, x, Key("7", N(7)), Key("8", N(8)), Key("9", N(9)), Key("+", VK_ADD) with { RowSpan = 2 });
            l.Add(top + 2, x, Key("4", N(4)), Key("5", N(5)), Key("6", N(6)));
            // The number pad's own Enter only presses Enter. The main one also closes the keyboard.
            l.Add(top + 3, x, Key("1", N(1)), Key("2", N(2)), Key("3", N(3)), Key("Enter", VK_RETURN, ext: true) with { RowSpan = 2 });
            l.Add(top + 4, x, Key("0", N(0), units: 2), Key(".", VK_DECIMAL));
        }
        return l;
    }

    /// <summary>
    /// The suggestion slots, evenly across the board, and the gear in the last column. With
    /// suggestions off the gear stands alone; it is still how they are turned back on. The window
    /// then draws this row in the grab bar's line rather than as a strip of its own, but to
    /// navigation it is the same row 0.
    /// </summary>
    private void AddBar(bool suggestions)
    {
        if (suggestions)
        {
            int n = SuggestionCount(Width);
            double w = (Width - 1) / n;
            for (int i = 0; i < n; i++)
                Slots.Add(new Slot(new KeyDef("", Action: KeyAction.Suggestion, Units: w, Hint: i == 0 ? "RS" : null) { Index = i }, 0, i * w));
        }
        Slots.Add(new Slot(Gear, 0, Width - 1));
    }

    private static readonly (KeyboardOption Option, string Name, string Detail)[] OptionRows =
    {
        (KeyboardOption.Suggestions, "Word suggestions", "Finish the word and guess the next one"),
        (KeyboardOption.FunctionKeys, "Function keys", "F1 to F12, Print Screen, Scroll Lock, Pause"),
        (KeyboardOption.NavKeys, "Navigation keys", "Insert, Delete, Home, End, Page Up and Down, arrows"),
        (KeyboardOption.Numpad, "Number pad", "The number pad of a full-size keyboard"),
        (KeyboardOption.Modifiers, "Ctrl, Win and Alt", "Each holds for the next key, like Shift"),
    };

    /// <summary>
    /// The options page: the size steps and the gear in the bar, and one switch a row. It is the
    /// same width and height as the keys it stands in for, so the keyboard does not jump when it
    /// opens -- only when a switch changes what the keys will be. The size lives in the bar because
    /// the rows are already as many as the plain keyboard has: a sixth would make the page taller
    /// than the keys. B goes back, as it does everywhere in the launcher; a B badge on the gear was
    /// tried and sat on top of the icon, the bar being shorter than a key.
    /// </summary>
    public static KeyboardLayout Options(KeyboardOptions o)
    {
        var l = new KeyboardLayout { Width = WidthFor(o), BodyRows = BodyRowsFor(o) };
        l.Slots.Add(new Slot(SizeDown, 0, SizeDownX(l.Width)));
        l.Slots.Add(new Slot(SizeUp, 0, SizeUpX(l.Width)));
        l.Slots.Add(new Slot(Gear, 0, l.Width - 1));
        double w = Math.Min(l.Width, MainWidth), x = (l.Width - w) / 2;
        int row = 1;
        foreach (var (option, name, detail) in OptionRows)
            l.Slots.Add(new Slot(new KeyDef(name, Action: KeyAction.Option, Units: w) { Option = option, Detail = detail }, row++, x));
        return l;
    }

    // ---- moving around ----

    public int Rows => 1 + BodyRows;

    public IEnumerable<Slot> InRow(int row) => Slots.Where(s => s.Covers(row)).OrderBy(s => s.X);

    /// <summary>Left or right along the row, wrapping, across block gaps.</summary>
    public Slot Step(Slot from, int row, int dir)
    {
        var keys = InRow(row).ToList();
        int i = keys.IndexOf(from);
        return keys[((i < 0 ? 0 : i) + dir + keys.Count) % keys.Count];
    }

    /// <summary>
    /// Up or down to the key under <paramref name="wantX"/>, wrapping top to bottom. A row with
    /// nothing within half a key of it is stepped over -- the hole in the navigation block, the
    /// space above the number pad -- except the bar, which always takes you: Up from the top row
    /// lands on the bar however wide the keyboard is. A tall key is left at its far end.
    /// </summary>
    public (Slot Key, int Row)? Vertical(Slot from, int row, double wantX, int dir)
    {
        int r = row;
        for (int step = 0; step < Rows; step++)
        {
            r = (r + dir + Rows) % Rows;
            var keys = InRow(r).ToList();
            if (keys.Count == 0 || keys.Contains(from)) continue;
            var hit = keys.FirstOrDefault(s => s.DistanceTo(wantX) == 0)
                      ?? keys.MinBy(s => s.DistanceTo(wantX))!;
            if (r != 0 && hit.DistanceTo(wantX) > 0.5) continue;
            return (hit, r);
        }
        return null;
    }

    /// <summary>The key at a row and column, or the nearest in that row, for keeping the
    /// highlight in place when the board is rebuilt.</summary>
    public (Slot Key, int Row)? Near(int row, double x)
    {
        for (int r = Math.Clamp(row, 0, Rows - 1); r >= 0; r--)
        {
            var keys = InRow(r).ToList();
            if (keys.Count > 0) return (keys.MinBy(s => s.DistanceTo(x))!, r);
        }
        return null;
    }
}

using System.Windows.Automation;

namespace Loungepad.Services;

/// <summary>
/// Is the control that has the keyboard focus somewhere you type? Asked through UI Automation --
/// the same thing Windows' own touch keyboard and every screen reader ask -- after a controller
/// click outside the launcher, so the Loungepad keyboard can come up on a text field the way a
/// phone's does.
///
/// Read-only, and nothing but the focused element's type, state and whether its value can be
/// written: never its text. Run it off the UI thread: a UIA call is cross-process, and one into a
/// hung app waits.
/// </summary>
internal static class TextFieldProbe
{
    public enum Answer { No, Yes, NotYet }

    /// <summary>
    /// Text boxes are Edit, and an Edit is typed into unless it says it is read-only: an input, a
    /// textarea or a password field in a browser, a Win32 or WPF text box, a browser's address bar.
    /// Anything else counts only when its value or its text can be written: a Document is also what
    /// a browser calls the whole page (its value is the address, read-only); a rich text box (WPF's,
    /// Word's) and a page's contenteditable (a Group to Chromium) have text that says it is
    /// writable. A combo box only with writable text: a
    /// browser's &lt;select&gt; claims a writable value and is a list. A browser that had no
    /// accessibility tree until this asked answers with its bare window the first time, which is
    /// <see cref="Answer.NotYet"/>. Checked against Chromium in a WebView2 and against WPF and
    /// Win32 controls, and against throwaway Chrome and Firefox (Nov 2026).
    /// </summary>
    public static Answer Focused(int ownPid) => Classify(AutomationElement.FocusedElement, ownPid);

    /// <summary>
    /// Does the window in front have a system caret showing? Win32 edit controls, rich edits and
    /// most toolkits make one exactly while a text field has the focus, and Chrome and Firefox do too
    /// once their accessibility is on. Not one of ours. Cheap and needs no UI Automation.
    /// </summary>
    public static bool CaretInForeground(IntPtr ours)
    {
        var fg = Loungepad.Interop.NativeMethods.GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == ours) return false;
        uint thread = Loungepad.Interop.NativeMethods.GetWindowThreadProcessId(fg, out uint pid);
        if (thread == 0 || pid == (uint)Environment.ProcessId) return false;
        var info = new Loungepad.Interop.NativeMethods.GUITHREADINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Loungepad.Interop.NativeMethods.GUITHREADINFO>() };
        if (!Loungepad.Interop.NativeMethods.GetGUIThreadInfo(thread, ref info) || info.hwndCaret == IntPtr.Zero) return false;
        // A caret that exists but is hidden, or has no size, is not one anybody is typing at.
        const int GUI_CARETBLINKING = 0x1;
        return (info.flags & GUI_CARETBLINKING) != 0
            && info.rcCaret.Bottom > info.rcCaret.Top;
    }

    /// <summary>The rule itself, for any element: a harness asks it of controls it never focuses.</summary>
    public static Answer Classify(AutomationElement? el, int ownPid)
    {
        if (el is null) return Answer.No;
        var c = el.Current;
        if (c.ProcessId == ownPid || !c.IsEnabled) return Answer.No;
        var type = c.ControlType;
        if (type == ControlType.Edit) return Writable(el) == false ? Answer.No : Answer.Yes;
        // A text box that suggests as you type is a combo box to UI Automation: Firefox's address
        // bar, Google's search box and every role="combobox" input in Chrome and Firefox. Its text
        // is writable; a <select>'s value claims to be too, but a <select> has no text at all.
        if (type == ControlType.ComboBox) return TextWritable(el) ? Answer.Yes : Answer.No;
        if (type == ControlType.Document || type == ControlType.Group
            || type == ControlType.Custom || type == ControlType.Pane || type == ControlType.Text)
        {
            var writable = Writable(el);
            if (writable == true) return Answer.Yes;
            // No value, only text, and the text says whether it can be changed. A page or a
            // read-only viewer says it cannot, or has a read-only value.
            if (writable is null && type != ControlType.Pane && type != ControlType.Text && TextWritable(el)) return Answer.Yes;
            // Chromium and Electron build their tree on the first UIA request.
            if (type == ControlType.Pane && (c.ClassName ?? "").StartsWith("Chrome_", StringComparison.Ordinal))
                return Answer.NotYet;
        }
        return Answer.No;
    }

    private static bool TextWritable(AutomationElement el) =>
        el.TryGetCurrentPattern(TextPattern.Pattern, out var p) && p is TextPattern t
        && t.DocumentRange.GetAttributeValue(TextPattern.IsReadOnlyAttribute) is false;

    /// <summary>true or false when the element has a value pattern, null when it has none.</summary>
    private static bool? Writable(AutomationElement el)
    {
        if (el.TryGetCurrentPattern(ValuePattern.Pattern, out var p) && p is ValuePattern v)
            return !v.Current.IsReadOnly;
        return null;
    }
}

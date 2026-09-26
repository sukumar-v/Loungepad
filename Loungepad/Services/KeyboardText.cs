using System.Text;

namespace Loungepad.Services;

/// <summary>
/// What the Loungepad keyboard has typed into the app in front, as far as it can know.
///
/// The keyboard never reads the text field -- it only ever sends keys into it -- so the word being
/// typed is the run of letters at the end of what IT typed since it last lost track. It loses
/// track whenever the caret may have moved without it: a caret or navigation key, Enter, Tab, a
/// shortcut, a click in the app, focus moving to another window or control (see KeyboardWindow's
/// context check). A reset only ever makes the next suggestion insert rather than replace, so when
/// in doubt it resets: replacing on a stale idea of the word would delete somebody's text.
///
/// Nothing here is kept beyond the keyboard being open, and the tail is capped.
/// </summary>
public sealed class KeyboardText
{
    private const int MaxTail = 200;
    private const string Punctuation = ".,!?;:";

    private readonly StringBuilder _tail = new();

    /// <summary>The last thing typed was a suggestion's own trailing space, which punctuation
    /// typed straight after it swaps places with.</summary>
    public bool AutoSpace { get; private set; }

    public bool IsEmpty => _tail.Length == 0;

    public void Reset()
    {
        _tail.Clear();
        AutoSpace = false;
    }

    public void Typed(string text)
    {
        _tail.Append(text);
        if (_tail.Length > MaxTail) _tail.Remove(0, _tail.Length - MaxTail);
        AutoSpace = false;
    }

    /// <summary>
    /// One Backspace went to the app. With nothing of ours left to take it from, the character it
    /// deleted was never ours and there is nothing more to know.
    /// </summary>
    public void Backspaced()
    {
        if (_tail.Length > 0) _tail.Length--;
        AutoSpace = false;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '\'' or '’';

    /// <summary>The letters at the end of what was typed: the word the suggestions are for.</summary>
    public string CurrentWord
    {
        get
        {
            int i = _tail.Length;
            while (i > 0 && IsWordChar(_tail[i - 1])) i--;
            return _tail.ToString(i, _tail.Length - i);
        }
    }

    /// <summary>Up to <paramref name="max"/> whole words before the current one, oldest first, for
    /// next-word prediction. Nothing across a sentence end: what came before a full stop says little
    /// about the next word.</summary>
    public IReadOnlyList<string> PreviousWords(int max)
    {
        var before = _tail.ToString(0, _tail.Length - CurrentWord.Length);
        int stop = before.TrimEnd().LastIndexOfAny(new[] { '.', '!', '?' });
        if (stop >= 0 && stop == before.TrimEnd().Length - 1) return Array.Empty<string>();
        if (stop >= 0) before = before[(stop + 1)..];
        var words = new List<string>();
        var word = new StringBuilder();
        foreach (var c in before + " ")
        {
            if (IsWordChar(c)) { word.Append(c); continue; }
            if (word.Length > 0) words.Add(word.ToString());
            word.Clear();
        }
        return words.Skip(Math.Max(0, words.Count - max)).ToList();
    }

    /// <summary>Just after ". ", "! " or "? " that the keyboard typed itself.</summary>
    public bool AtSentenceStart
    {
        get
        {
            var before = _tail.ToString(0, _tail.Length - CurrentWord.Length);
            var t = before.TrimEnd();
            return t.Length > 0 && t.Length < before.Length && t[^1] is '.' or '!' or '?';
        }
    }

    /// <summary>
    /// What to send to put <paramref name="candidate"/> in place of the current word: how many
    /// Backspaces, then what to type (the rest of the word and a space). Only the part that differs
    /// is retyped -- "hel" to "hello" is "lo", not three Backspaces and "hello" -- and the typed
    /// word's capitals carry over: "Hel" gives Hello, "HEL" gives HELLO.
    /// </summary>
    public (int Backspaces, string Insert) Replacement(string candidate, bool capitalize)
    {
        var word = CurrentWord;
        var adapted = Cased(candidate, word, capitalize || (word.Length == 0 && AtSentenceStart));
        int common = 0;
        while (common < word.Length && common < adapted.Length && word[common] == adapted[common]) common++;
        return (word.Length - common, adapted[common..] + " ");
    }

    /// <summary>The candidate as the bar should show it for this word.</summary>
    public string Display(string candidate, bool capitalize) =>
        Cased(candidate, CurrentWord, capitalize || (CurrentWord.Length == 0 && AtSentenceStart));

    private static string Cased(string candidate, string word, bool capitalize)
    {
        if (candidate.Length == 0) return candidate;
        int letters = word.Count(char.IsLetter);
        if (letters >= 2 && word.Where(char.IsLetter).All(char.IsUpper)) return candidate.ToUpperInvariant();
        if (capitalize || (word.Length > 0 && char.IsUpper(word[0])))
            return char.ToUpperInvariant(candidate[0]) + candidate[1..];
        return candidate;
    }

    /// <summary>A suggestion went in: <paramref name="backspaces"/> then <paramref name="insert"/>.</summary>
    public void Replaced(int backspaces, string insert)
    {
        for (int i = 0; i < backspaces; i++) Backspaced();
        Typed(insert);
        AutoSpace = insert.EndsWith(' ');
    }

    /// <summary>
    /// Punctuation straight after a suggestion's space goes before it instead -- "hello" + "," is
    /// "hello, " rather than "hello ," -- which is what every phone keyboard does, and what the
    /// space was only ever standing in for. Null when the character is to be typed as it is.
    /// </summary>
    public string? PunctuationSwap(char c) =>
        AutoSpace && Punctuation.Contains(c) ? c + " " : null;

    /// <summary>A swap went in: one Backspace, then the punctuation and the space. The space is
    /// still the suggestion's, so "..." or "?!" keeps going in front of it.</summary>
    public void Punctuated(string swapped)
    {
        Backspaced();
        Typed(swapped);
        AutoSpace = true;
    }
}

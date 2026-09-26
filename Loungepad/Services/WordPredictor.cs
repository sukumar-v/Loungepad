using Windows.Data.Text;
using Windows.Globalization;
using Windows.System.UserProfile;

namespace Loungepad.Services;

/// <summary>
/// Word suggestions for the Loungepad keyboard, from Windows' own text prediction -- the engine
/// behind the touch keyboard's suggestion bar (Windows.Data.Text.TextPredictionGenerator). It works
/// from an unpackaged desktop app, needs no key and no network, answers in a few milliseconds, and
/// learns nothing from what it is asked: nothing typed on the keyboard is kept anywhere.
///
/// Asked for <see cref="TextPredictionOptions.Predictions"/> only. That mode completes the word
/// ("hel" → hello, help), fixes spelling ("recieve" → receive) and, given the words before, guesses
/// the next one ("see you" → tomorrow, soon). <see cref="TextPredictionOptions.Corrections"/> is
/// built for a touch screen, where a finger lands between keys, so it offers the NEIGHBOURS of what
/// was typed -- "hel" → yep, gel -- and a pad lands on exactly the key it meant. The one thing
/// worth taking from it is the apostrophe nobody types on a pad (dont → don't, im → I'm), which
/// Predictions never offers; those are picked out of its answer by letters alone.
/// </summary>
public sealed class WordPredictor
{
    private TextPredictionGenerator? _gen;
    private bool _failed;
    private readonly object _lock = new();
    /// <summary>One question at a time. Typing fast asks faster than the engine answers, and
    /// nothing says the generator takes concurrent calls; the keyboard drops stale answers anyway.</summary>
    private readonly SemaphoreSlim _one = new(1, 1);

    /// <summary>Suggestions for the word being typed, best first. Empty on any failure.</summary>
    public async Task<IReadOnlyList<string>> SuggestAsync(string word, IReadOnlyList<string> previous, int max)
    {
        var gen = Generator();
        if (gen is null) return Array.Empty<string>();
        await _one.WaitAsync();
        try
        {
            // More than are wanted: some are dropped below, and the engine's list for a small
            // count is not simply the head of its list for a bigger one.
            var predicted = await gen.GetCandidatesAsync(word, (uint)(max * 2 + 2), TextPredictionOptions.Predictions, previous);
            IReadOnlyList<string> fixes = Array.Empty<string>();
            if (word.Length >= 2 && !word.Contains('\''))
            {
                var corrected = await gen.GetCandidatesAsync(word, 6, TextPredictionOptions.Corrections, Array.Empty<string>());
                fixes = corrected.Where(c => c.Contains('\'') && Unapostrophed(c).Equals(word, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            return Pick(word, fixes.Concat(predicted), max);
        }
        catch (Exception ex)
        {
            Log.Info($"Word suggestions failed: {ex.Message}");
            return Array.Empty<string>();
        }
        finally { _one.Release(); }
    }

    /// <summary>
    /// What the bar shows, from what the engine answered. Candidates with a space are dropped (they
    /// are its guesses at two short words run together), as is anything that is only the typed word
    /// again. A re-casing of the typed word -- "i" → I -- is kept only when the engine puts it
    /// first, which is how "i" still becomes "I" while "th" does not offer "Th" and "TH".
    /// </summary>
    internal static IReadOnlyList<string> Pick(string word, IEnumerable<string> candidates, int max)
    {
        var picked = new List<string>();
        bool first = true;
        foreach (var raw in candidates)
        {
            var c = raw?.Trim() ?? "";
            bool wasFirst = first;
            first = false;
            if (c.Length == 0 || c.Any(char.IsWhiteSpace)) continue;
            if (c == word) continue;
            if (c.Equals(word, StringComparison.OrdinalIgnoreCase) && !wasFirst) continue;
            if (picked.Any(p => p.Equals(c, StringComparison.OrdinalIgnoreCase))) continue;
            picked.Add(c);
            if (picked.Count == max) break;
        }
        return picked;
    }

    private static string Unapostrophed(string s) => s.Replace("'", "").Replace("’", "");

    private TextPredictionGenerator? Generator()
    {
        lock (_lock)
        {
            if (_gen is not null || _failed) return _gen;
            try
            {
                var tag = Language();
                _gen = new TextPredictionGenerator(tag);
                Log.Info($"Word suggestions: {tag}");
            }
            catch (Exception ex)
            {
                _failed = true;
                Log.Info($"Word suggestions unavailable: {ex.Message}");
            }
            return _gen;
        }
    }

    /// <summary>
    /// The first of the user's Windows languages written in Latin script, since that is what the
    /// keys type -- a Hindi dictionary asked about Latin letters has nothing to say. English when
    /// none is.
    /// </summary>
    private static string Language()
    {
        try
        {
            foreach (var tag in GlobalizationPreferences.Languages)
                if (new Language(tag).Script == "Latn") return tag;
        }
        catch { }
        return "en-US";
    }
}

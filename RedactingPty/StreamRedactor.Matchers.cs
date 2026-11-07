using System.Text;

namespace RedactingPty;

// The matcher path: used only when at least one IStreamMatcher is given. With no matcher the
// redactor keeps the 1.0 algorithm (successive Replace, overlap = longest Find - 1) unchanged, for
// backward compatibility.
//
// Invariant: no character of a complete occurrence (every occurrence of every Find, overlapping ones
// included, and every matcher match) is ever emitted. In the part that is emitted, overlapping
// occurrences (sharing at least one character) merge into maximal runs; each run is written once as
// the label of its longest occurrence, then the leftmost, then plaintext before matcher, then list
// order. Adjacent but disjoint occurrences stay separate runs. Replacement text is never matched again.
public sealed partial class StreamRedactor
{
    private readonly IStreamMatcher[]? _matchers;
    private readonly int[] _maxMatchLengths = [];
    // Knuth-Morris-Pratt prefix tables of the Finds, for the exact plaintext pending point.
    private readonly int[][] _prefixTables = [];
    private readonly List<StreamMatch> _found = [];
    private readonly List<Occurrence> _occurrences = [];

    private readonly record struct Occurrence(int Start, int Length, string Replacement, int Order)
    {
        public int End => Start + Length;
    }

    /// <summary>
    /// Builds a redactor that masks the plaintext <paramref name="replacements"/> and every match the
    /// <paramref name="matchers"/> report. With an empty <paramref name="matchers"/> list it behaves
    /// exactly like <see cref="StreamRedactor(IReadOnlyList{StreamReplacement})"/>.
    /// </summary>
    /// <exception cref="ArgumentException">A matcher is null or its <see cref="IStreamMatcher.MaxMatchLength"/> is below 1.</exception>
    public StreamRedactor(IReadOnlyList<StreamReplacement> replacements, IReadOnlyList<IStreamMatcher> matchers)
        : this(replacements)
    {
        ArgumentNullException.ThrowIfNull(matchers);
        if (matchers.Count == 0)
            return;

        _matchers = new IStreamMatcher[matchers.Count];
        _maxMatchLengths = new int[matchers.Count];
        for (var i = 0; i < matchers.Count; i++)
        {
            var matcher = matchers[i] ?? throw new ArgumentException("Matcher entries must not be null.", nameof(matchers));
            var max = matcher.MaxMatchLength;
            if (max < 1)
                throw new ArgumentException($"{matcher.GetType().FullName}.MaxMatchLength is {max}; it must be at least 1.", nameof(matchers));
            _matchers[i] = matcher;
            _maxMatchLengths[i] = max;
        }
        _prefixTables = [.. _replacements.Select(r => PrefixTable(r.Find))];
    }

    /// <summary>True while a non-empty tail is held back, waiting for the next chunk.</summary>
    public bool HasHeld => _tail.Length > 0;

    /// <summary>
    /// Resolves the held tail as <see cref="Flush"/> does (complete matches masked) and returns it;
    /// the redactor stays usable and the next chunk starts with an empty tail. Text that lies in no
    /// complete occurrence, a partial one included, is released as it is. Returns <c>""</c> when
    /// nothing is held.
    /// </summary>
    public string Release()
    {
        if (_matchers is not null)
            return ResolveTail();
        var result = _tail.Length > 0 ? ApplyReplacements(_tail) : "";
        _tail = "";
        return result;
    }

    private string ProcessWithMatchers(string chunk)
    {
        var window = _tail + chunk;
        CollectOccurrences(window);

        var hold = PlaintextPendingFrom(window);
        for (var i = 0; i < _matchers!.Length; i++)
        {
            var pending = _matchers[i].PendingFrom(window);
            if (pending < 0 || pending > window.Length)
                throw new InvalidOperationException(
                    $"{_matchers[i].GetType().FullName}.PendingFrom returned {pending} for a window of {window.Length} characters; it must lie in [0, {window.Length}].");
            hold = Math.Min(hold, pending);
        }

        // Pull the hold point back to the start of any occurrence that straddles it, and never split a
        // surrogate pair, until neither moves it: each pass re-checks every occurrence, so a chain of
        // overlapping occurrences across the boundary moves as one.
        bool adjusted;
        do
        {
            adjusted = false;
            foreach (var o in _occurrences)
            {
                if (o.Start < hold && o.End > hold)
                {
                    hold = o.Start;
                    adjusted = true;
                }
            }
            if (hold > 0 && hold < window.Length &&
                char.IsHighSurrogate(window[hold - 1]) && char.IsLowSurrogate(window[hold]))
            {
                hold--;
                adjusted = true;
            }
        } while (adjusted);

        _tail = window[hold..];
        return Mask(window, hold);
    }

    private string ResolveTail()
    {
        var window = _tail;
        _tail = "";
        if (window.Length == 0)
            return "";
        CollectOccurrences(window);
        return Mask(window, window.Length);
    }

    private void CollectOccurrences(string window)
    {
        _occurrences.Clear();
        for (var r = 0; r < _replacements.Count; r++)
        {
            var find = _replacements[r].Find;
            if (find.Length == 0) continue;
            for (var idx = window.IndexOf(find, StringComparison.Ordinal); idx >= 0;
                 idx = idx + 1 < window.Length ? window.IndexOf(find, idx + 1, StringComparison.Ordinal) : -1)
                _occurrences.Add(new Occurrence(idx, find.Length, _replacements[r].Replace, r));
        }

        var order = _replacements.Count;
        for (var m = 0; m < _matchers!.Length; m++)
        {
            _found.Clear();
            _matchers[m].FindMatches(window, _found);
            foreach (var match in _found)
            {
                // Indices, lengths and the matcher's type only: never the window's or the match's text.
                if (match.Start < 0 || match.Length < 1 || match.Length > _maxMatchLengths[m] ||
                    match.Start > window.Length - match.Length)
                    throw new InvalidOperationException(
                        $"{_matchers[m].GetType().FullName} reported a match at {match.Start} of length {match.Length} "
                        + $"in a window of {window.Length} characters (MaxMatchLength {_maxMatchLengths[m]}).");
                if (match.Replacement is null)
                    throw new InvalidOperationException(
                        $"{_matchers[m].GetType().FullName} reported a match at {match.Start} of length {match.Length} with a null Replacement.");
                _occurrences.Add(new Occurrence(match.Start, match.Length, match.Replacement, order++));
            }
        }
    }

    /// <summary>
    /// The smallest index whose suffix of <paramref name="window"/> is a proper prefix of some Find:
    /// exact, so the plaintext part holds back only text that could still become an occurrence.
    /// </summary>
    private int PlaintextPendingFrom(string window)
    {
        var pending = window.Length;
        for (var r = 0; r < _replacements.Count; r++)
        {
            var find = _replacements[r].Find;
            if (find.Length < 2) continue;
            var table = _prefixTables[r];
            var q = 0;
            for (var i = Math.Max(0, window.Length - (find.Length - 1)); i < window.Length; i++)
            {
                var c = window[i];
                while (q > 0 && find[q] != c) q = table[q - 1];
                if (find[q] == c) q++;
                if (q == find.Length) q = table[q - 1];
            }
            pending = Math.Min(pending, window.Length - q);
        }
        return pending;
    }

    private static int[] PrefixTable(string find)
    {
        var table = new int[find.Length];
        for (int i = 1, k = 0; i < find.Length; i++)
        {
            while (k > 0 && find[i] != find[k]) k = table[k - 1];
            if (find[i] == find[k]) k++;
            table[i] = k;
        }
        return table;
    }

    /// <summary>
    /// <paramref name="window"/>[..<paramref name="length"/>] with every maximal run of overlapping
    /// complete occurrences replaced, in one pass, by the label the rule picks.
    /// </summary>
    private string Mask(string window, int length)
    {
        _occurrences.RemoveAll(o => o.End > length);
        if (_occurrences.Count == 0)
            return window[..length];

        _occurrences.Sort(static (x, y) => x.Start.CompareTo(y.Start));
        var result = new StringBuilder(length);
        var emitted = 0;
        var i = 0;
        while (i < _occurrences.Count)
        {
            var best = _occurrences[i];
            var runStart = best.Start;
            var runEnd = best.End;
            for (i++; i < _occurrences.Count && _occurrences[i].Start < runEnd; i++)
            {
                var o = _occurrences[i];
                runEnd = Math.Max(runEnd, o.End);
                if (Ranks(o, best))
                    best = o;
            }
            result.Append(window, emitted, runStart - emitted).Append(best.Replacement);
            emitted = runEnd;
        }
        result.Append(window, emitted, length - emitted);
        return result.ToString();
    }

    /// <summary>Whether <paramref name="x"/> labels a run before <paramref name="y"/>: longest, leftmost, then order.</summary>
    private static bool Ranks(Occurrence x, Occurrence y) =>
        x.Length != y.Length ? x.Length > y.Length
        : x.Start != y.Start ? x.Start < y.Start
        : x.Order < y.Order;
}

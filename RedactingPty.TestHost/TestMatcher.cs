using RedactingPty;

namespace RedactingPty.TestHost;

/// <summary>
/// The test host's <see cref="IStreamMatcher"/>: finds literal strings (a stand-in for a matcher that
/// knows its values only by fingerprint), optionally holds every character (a stand-in for a
/// multi-line vault secret, which may start anywhere), and optionally throws on its Nth call.
/// </summary>
internal sealed class TestMatcher(IReadOnlyList<(string Find, string Label)> finds, int? throwOnCall, bool holdAll) : IStreamMatcher
{
    /// <summary>The exception text: it names no output.</summary>
    public const string FailureMessage = "test matcher failure";

    private int _calls;

    public int MaxMatchLength { get; } = holdAll ? 5000 : Math.Max(1, finds.Count == 0 ? 1 : finds.Max(f => f.Find.Length));

    public void FindMatches(ReadOnlySpan<char> window, ICollection<StreamMatch> matches)
    {
        if (Interlocked.Increment(ref _calls) == throwOnCall)
            throw new InvalidOperationException(FailureMessage);
        foreach (var (find, label) in finds)
        {
            var offset = 0;
            for (var at = window.IndexOf(find, StringComparison.Ordinal); at >= 0;
                 at = window[offset..].IndexOf(find, StringComparison.Ordinal))
            {
                matches.Add(new StreamMatch(offset + at, find.Length, label));
                offset += at + 1;
            }
        }
    }

    public int PendingFrom(ReadOnlySpan<char> window) =>
        holdAll ? 0 : Math.Max(0, window.Length - (MaxMatchLength - 1));
}

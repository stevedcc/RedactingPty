using System.Text;
using RedactingPty;
using Xunit;

namespace RedactingPty.Tests;

/// <summary>
/// The redactor's matcher path: <see cref="StreamRedactor(IReadOnlyList{StreamReplacement}, IReadOnlyList{IStreamMatcher})"/>
/// with at least one <see cref="IStreamMatcher"/>. Invariant under test: no character of a complete
/// occurrence (plaintext <c>Find</c> or matcher match) is ever emitted, however the output is chunked;
/// overlapping occurrences merge into one run with one label (longest, then leftmost, then plaintext
/// before matcher, then list order).
/// </summary>
public class StreamRedactorMatcherTests
{
    // ── test matchers ────────────────────────────────────────────────────────────────

    /// <summary>Finds every occurrence of literal strings; the default <c>PendingFrom</c>.</summary>
    private sealed class LiteralMatcher(params (string Find, string Label)[] finds) : IStreamMatcher
    {
        public int MaxMatchLength { get; init; } = finds.Length == 0 ? 1 : finds.Max(f => f.Find.Length);

        public void FindMatches(ReadOnlySpan<char> window, ICollection<StreamMatch> matches)
        {
            foreach (var (find, label) in finds)
            {
                for (var i = window.IndexOf(find.AsSpan(), StringComparison.Ordinal); i >= 0;)
                {
                    matches.Add(new StreamMatch(i, find.Length, label));
                    var next = window[(i + 1)..].IndexOf(find.AsSpan(), StringComparison.Ordinal);
                    i = next < 0 ? -1 : i + 1 + next;
                }
            }
        }
    }

    /// <summary>Reports fixed matches and a fixed pending point (a function of the window).</summary>
    private sealed class ScriptedMatcher(int maxMatchLength) : IStreamMatcher
    {
        public Func<string, IEnumerable<StreamMatch>> Matches { get; init; } = _ => [];
        public Func<string, int>? Pending { get; init; }
        public int MaxMatchLength => maxMatchLength;

        public void FindMatches(ReadOnlySpan<char> window, ICollection<StreamMatch> matches)
        {
            foreach (var m in Matches(window.ToString()))
                matches.Add(m);
        }

        public int PendingFrom(ReadOnlySpan<char> window) =>
            Pending is null ? Math.Max(0, window.Length - (MaxMatchLength - 1)) : Pending(window.ToString());
    }

    /// <summary>Holds nothing and finds nothing: puts a redactor on the matcher path without matching.</summary>
    private static ScriptedMatcher Inert() => new(1) { Pending = w => w.Length };

    private static List<string> Pieces(StreamRedactor redactor, IEnumerable<string> chunks)
    {
        List<string> pieces = [];
        foreach (var c in chunks)
            pieces.Add(redactor.ProcessChunk(c));
        pieces.Add(redactor.Flush());
        return pieces;
    }

    private static string Run(StreamRedactor redactor, params string[] chunks) => string.Concat(Pieces(redactor, chunks));

    private static IEnumerable<string> Chunk(string text, int size)
    {
        for (var i = 0; i < text.Length; i += size)
            yield return text.Substring(i, Math.Min(size, text.Length - i));
    }

    private static string RandomText(Random rng, int length, string alphabet)
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = alphabet[rng.Next(alphabet.Length)];
        return new string(chars);
    }

    // ── splitting ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Matcher_SplitAtEveryPosition_Masked()
    {
        var secret = RandomText(new Random(1), 40, "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789");
        var text = "pre " + secret + " post";
        for (var cut = 0; cut <= text.Length; cut++)
        {
            var redactor = new StreamRedactor([], [new LiteralMatcher((secret, "[M]"))]);
            Assert.Equal("pre [M] post", Run(redactor, text[..cut], text[cut..]));
        }
    }

    [Fact]
    public void Matcher_PemSized5000Chars_AcrossChunks_Masked()
    {
        var secret = RandomText(new Random(2), 5000, "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/\n");
        var filler = new string('.', 3000);
        var text = filler + secret + filler;
        var redactor = new StreamRedactor([], [new LiteralMatcher((secret, "[PEM]"))]);

        Assert.Equal(filler + "[PEM]" + filler, string.Concat(Pieces(redactor, Chunk(text, 4096))));
    }

    // ── overlaps and labels ──────────────────────────────────────────────────────────

    [Fact]
    public void Matcher_OverlapsPlaintext_LongestWins()
    {
        var matcherLonger = new StreamRedactor([new StreamReplacement("abcd", "[P]")], [new LiteralMatcher(("bcdefg", "[M]"))]);
        Assert.Equal("x [M] y", Run(matcherLonger, "x abcdefg y"));

        var plaintextLonger = new StreamRedactor([new StreamReplacement("abcdefg", "[P]")], [new LiteralMatcher(("efgh", "[M]"))]);
        Assert.Equal("x [P] y", Run(plaintextLonger, "x abcdefgh y"));
    }

    [Fact]
    public void Matcher_EqualLength_PlaintextWins()
    {
        var redactor = new StreamRedactor([new StreamReplacement("abcd", "[P]")], [new LiteralMatcher(("abcd", "[M]"))]);
        Assert.Equal("x [P] y", Run(redactor, "x abcd y"));

        // Equal length, different starts: leftmost decides before the source does.
        var leftmostMatcher = new StreamRedactor([new StreamReplacement("bcde", "[P]")], [new LiteralMatcher(("abcd", "[M]"))]);
        Assert.Equal("x [M] y", Run(leftmostMatcher, "x abcde y"));
    }

    [Fact]
    public void Matcher_Nested_LongerWins()
    {
        var plaintextInside = new StreamRedactor([new StreamReplacement("cd", "[P]")], [new LiteralMatcher(("abcdef", "[M]"))]);
        Assert.Equal("x [M] y", Run(plaintextInside, "x abcdef y"));

        var matcherInside = new StreamRedactor([new StreamReplacement("abcdef", "[P]")], [new LiteralMatcher(("cd", "[M]"))]);
        Assert.Equal("x [P] y", Run(matcherInside, "x abcdef y"));
    }

    [Fact]
    public void Matcher_Disjoint_AllMasked()
    {
        var redactor = new StreamRedactor([new StreamReplacement("plain-one", "[P]")], [new LiteralMatcher(("fp-two", "[M]"))]);
        Assert.Equal("a [M] b [P] c [M]", Run(redactor, "a fp-two b plain-one c fp-two"));
    }

    [Fact]
    public void Matcher_ReplacementTextNotRescanned()
    {
        // The first label contains the second Find: a Replace pass after the span pass would mask it again.
        var redactor = new StreamRedactor(
            [new StreamReplacement("secret", "[REDACTED: token]"), new StreamReplacement("token", "[T]")], [Inert()]);
        Assert.Equal("a [REDACTED: token] b [T]", Run(redactor, "a secret b token"));

        var matcherLabel = new StreamRedactor([new StreamReplacement("name", "[N]")], [new LiteralMatcher(("value", "[REDACTED: name]"))]);
        Assert.Equal("[REDACTED: name] [N]", Run(matcherLabel, "value name"));
    }

    [Fact]
    public void Matcher_Straddler_NeverEmittedInPart()
    {
        // "abcXY" (matcher) is complete in the first window, but "XY" is a proper prefix of the plaintext
        // "XYZ", so the hold point falls inside the matcher's span: it must be pulled back to its start.
        var redactor = new StreamRedactor([new StreamReplacement("XYZ", "[P]")], [new LiteralMatcher(("abcXY", "[M]")) { MaxMatchLength = 5 }]);

        var pieces = Pieces(redactor, ["pre abcXY", "Z post"]);

        foreach (var piece in pieces)
            Assert.DoesNotContain(piece, c => c is 'a' or 'b' or 'c' or 'X' or 'Y' or 'Z');
        Assert.Equal("pre [M] post", string.Concat(pieces));
    }

    // ── the hold point ───────────────────────────────────────────────────────────────

    [Fact]
    public void Matcher_PendingFromWindowLength_EmitsAtOnce()
    {
        var redactor = new StreamRedactor([], [new ScriptedMatcher(100) { Pending = w => w.Length }]);

        Assert.Equal("hello", redactor.ProcessChunk("hello"));
        Assert.False(redactor.HasHeld);
    }

    [Fact]
    public void Plaintext_NewPath_HoldsOnlyARealPrefix()
    {
        var redactor = new StreamRedactor([new StreamReplacement("secret", "[S]")], [Inert()]);

        Assert.Equal("hello ", redactor.ProcessChunk("hello sec"));
        Assert.Equal("[S] and xyz", redactor.ProcessChunk("ret and xyz"));
        Assert.Equal("", redactor.Flush());
    }

    // ── validation ───────────────────────────────────────────────────────────────────

    private const string SensitiveWindow = "SENSITIVE-OUTPUT";

    [Theory]
    [InlineData(-1, 2)]   // negative start
    [InlineData(10, 7)]   // ends past the window (16 chars)
    [InlineData(3, 0)]    // empty
    [InlineData(0, 9)]    // longer than MaxMatchLength (8)
    public void Matcher_InvalidMatch_Throws(int start, int length)
    {
        var matcher = new ScriptedMatcher(8) { Matches = _ => [new StreamMatch(start, length, "[M]")], Pending = w => w.Length };
        var redactor = new StreamRedactor([], [matcher]);

        var ex = Assert.Throws<InvalidOperationException>(() => redactor.ProcessChunk(SensitiveWindow));

        Assert.Contains(nameof(ScriptedMatcher), ex.Message);
        AssertNoWindowText(ex.Message);
    }

    [Fact]
    public void Matcher_NullReplacement_Throws()
    {
        var matcher = new ScriptedMatcher(8) { Matches = _ => [new StreamMatch(0, 3, null!)], Pending = w => w.Length };
        var redactor = new StreamRedactor([], [matcher]);

        var ex = Assert.Throws<InvalidOperationException>(() => redactor.ProcessChunk(SensitiveWindow));
        AssertNoWindowText(ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void Matcher_MaxMatchLengthBelowOne_Throws(int maxMatchLength)
    {
        Assert.Throws<ArgumentException>(() => new StreamRedactor([], [new ScriptedMatcher(maxMatchLength)]));
    }

    [Fact]
    public void Matcher_NullMatcher_Throws()
    {
        Assert.Throws<ArgumentException>(() => new StreamRedactor([], [null!]));
        Assert.Throws<ArgumentNullException>(() => new StreamRedactor([], null!));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(17)] // window is 16 chars
    public void Matcher_PendingFromOutOfRange_Throws(int pending)
    {
        var redactor = new StreamRedactor([], [new ScriptedMatcher(8) { Pending = _ => pending }]);

        var ex = Assert.Throws<InvalidOperationException>(() => redactor.ProcessChunk(SensitiveWindow));

        Assert.Contains(nameof(ScriptedMatcher), ex.Message);
        AssertNoWindowText(ex.Message);
    }

    private static void AssertNoWindowText(string message)
    {
        for (var i = 0; i + 4 <= SensitiveWindow.Length; i++)
            Assert.DoesNotContain(SensitiveWindow.Substring(i, 4), message);
    }

    // ── union of occurrences ─────────────────────────────────────────────────────────

    [Fact]
    public void Matcher_OverlappingOccurrences_UnionMasked()
    {
        var redactor = new StreamRedactor([new StreamReplacement("abcd", "[A]")], [new LiteralMatcher(("cdef", "[C]"))]);

        Assert.Equal("x [A] y", Run(redactor, "x abcdef y"));
    }

    [Fact]
    public void Matcher_EqualSpans_OneMaskByTieOrder()
    {
        var twoMatchers = new StreamRedactor([], [new LiteralMatcher(("same", "[first]")), new LiteralMatcher(("same", "[second]"))]);
        Assert.Equal("x [first] y", Run(twoMatchers, "x same y"));

        var oneMatcher = new StreamRedactor([], [new LiteralMatcher(("same", "[one]"), ("same", "[two]"))]);
        Assert.Equal("x [one] y", Run(oneMatcher, "x same y"));

        var twoPlaintexts = new StreamRedactor([new StreamReplacement("same", "[p1]"), new StreamReplacement("same", "[p2]")], [Inert()]);
        Assert.Equal("x [p1] y", Run(twoPlaintexts, "x same y"));
    }

    public static TheoryData<string[], string[], string> Chains => new()
    {
        // Equal lengths, all matcher: one run, the leftmost occurrence's label.
        { [], ["abcd:[1]", "defg:[2]", "ghij:[3]"], "<[1]>" },
        // Unequal lengths: the longest occurrence's label.
        { [], ["abcd:[1]", "defgh:[2]", "hij:[3]"], "<[2]>" },
        // Plaintext and matcher mixed: the longest is the plaintext one.
        { ["cdefg:[P]"], ["abcd:[1]", "ghij:[3]"], "<[P]>" },
        // An equal-length tie at the same start: plaintext labels it.
        { ["abcd:[P]"], ["abcd:[1]", "defg:[2]", "ghij:[3]"], "<[P]>" },
    };

    [Theory]
    [MemberData(nameof(Chains))]
    public void Matcher_ChainedOverlaps_UnionMasked(string[] plaintext, string[] matcher, string expected)
    {
        var redactor = new StreamRedactor(
            [.. plaintext.Select(p => p.Split(':')).Select(p => new StreamReplacement(p[0], p[1]))],
            [new LiteralMatcher([.. matcher.Select(m => m.Split(':')).Select(m => (m[0], m[1]))])]);

        Assert.Equal(expected, Run(redactor, "<abcdefghij>"));
    }

    [Fact]
    public void Matcher_AdjacentOccurrences_TwoLabels()
    {
        var redactor = new StreamRedactor([new StreamReplacement("abcd", "[A]")], [new LiteralMatcher(("efgh", "[E]"))]);

        Assert.Equal("<[A][E]>", Run(redactor, "<abcdefgh>"));
    }

    /// <summary>
    /// Property: occurrences drawn from an alphabet (Greek) that filler and labels never use, planted
    /// as overlapping chains, found by plaintext finds and by a matcher, under random chunkings. No
    /// Greek character may survive, and the output equals a whole-text reference that merges every
    /// occurrence into maximal runs and labels each by the rule.
    /// </summary>
    [Fact]
    public void Matcher_RandomOverlaps_NoOccurrenceCharacterSurvives()
    {
        const string greek = "αβγδεζηθικλμνξοπρστυφχψω";
        const string filler = "abcdefghijklmnopqrstuvwxyz \n";
        var rng = new Random(20261107);
        for (var round = 0; round < 400; round++)
        {
            List<(string Find, string Label, bool Plaintext)> finds = [];
            var text = new StringBuilder();
            var regions = rng.Next(1, 5);
            for (var r = 0; r < regions; r++)
            {
                text.Append(RandomText(rng, rng.Next(0, 12), filler));
                var region = RandomText(rng, rng.Next(3, 30), greek);
                text.Append(region);
                // Cover the region with occurrences that overlap or touch their neighbours.
                for (var start = 0; start < region.Length;)
                {
                    var length = rng.Next(1, Math.Min(10, region.Length - start) + 1);
                    finds.Add((region.Substring(start, length), $"[L{finds.Count}]", rng.Next(2) == 0));
                    var advance = rng.Next(1, length + 1);
                    start += advance;
                }
            }
            text.Append(RandomText(rng, rng.Next(0, 12), filler));
            var full = text.ToString();

            var plaintext = finds.Where(f => f.Plaintext).Select(f => new StreamReplacement(f.Find, f.Label)).ToList();
            var matcherFinds = finds.Where(f => !f.Plaintext).Select(f => (f.Find, f.Label)).ToArray();
            var expected = Reference(full, plaintext, matcherFinds);

            for (var chunking = 0; chunking < 4; chunking++)
            {
                var redactor = new StreamRedactor(plaintext, [new LiteralMatcher(matcherFinds)]);
                List<string> chunks = [];
                for (var i = 0; i < full.Length;)
                {
                    var size = rng.Next(1, 8);
                    chunks.Add(full.Substring(i, Math.Min(size, full.Length - i)));
                    i += size;
                }
                var pieces = Pieces(redactor, chunks);
                var output = string.Concat(pieces);

                Assert.DoesNotContain(output, c => greek.Contains(c));
                Assert.Equal(expected, output);
            }
        }
    }

    /// <summary>The masking rule applied to a whole text at once (the oracle for the property test).</summary>
    private static string Reference(string text, List<StreamReplacement> plaintext, (string Find, string Label)[] matcherFinds)
    {
        List<(int Start, int Length, string Label, int Order)> occurrences = [];
        var order = 0;
        // Plaintext in the redactor's own order: longest Find first, stable among equal lengths.
        foreach (var p in plaintext.OrderByDescending(p => p.Find.Length))
        {
            for (var i = text.IndexOf(p.Find, StringComparison.Ordinal); i >= 0; i = text.IndexOf(p.Find, i + 1, StringComparison.Ordinal))
                occurrences.Add((i, p.Find.Length, p.Replace, order));
            order++;
        }
        foreach (var (find, label) in matcherFinds)
        {
            for (var i = text.IndexOf(find, StringComparison.Ordinal); i >= 0; i = text.IndexOf(find, i + 1, StringComparison.Ordinal))
                occurrences.Add((i, find.Length, label, order));
            order++;
        }

        var covered = new bool[text.Length];
        foreach (var o in occurrences)
            for (var i = o.Start; i < o.Start + o.Length; i++)
                covered[i] = true;

        var result = new StringBuilder();
        for (var i = 0; i < text.Length;)
        {
            if (!covered[i]) { result.Append(text[i]); i++; continue; }
            // A maximal run: grow while some occurrence that shares a character with the run extends it.
            var runStart = i;
            var runEnd = i;
            bool grew;
            do
            {
                grew = false;
                foreach (var o in occurrences)
                {
                    if (o.Start < Math.Max(runEnd, runStart + 1) && o.Start + o.Length > runStart && o.Start + o.Length > runEnd)
                    {
                        runEnd = o.Start + o.Length;
                        grew = true;
                    }
                }
            } while (grew);
            var label = occurrences
                .Where(o => o.Start >= runStart && o.Start + o.Length <= runEnd)
                .OrderByDescending(o => o.Length).ThenBy(o => o.Start).ThenBy(o => o.Order)
                .First().Label;
            result.Append(label);
            i = runEnd;
        }
        return result.ToString();
    }

    // ── flush, surrogates ────────────────────────────────────────────────────────────

    [Fact]
    public void Matcher_Flush_Masks()
    {
        var redactor = new StreamRedactor([], [new LiteralMatcher(("TAILSECRET", "[M]"))]);

        var emitted = redactor.ProcessChunk("end TAILSECRET");
        Assert.DoesNotContain("TAILSECRET", emitted);
        Assert.Equal("end [M]", emitted + redactor.Flush());
    }

    [Fact]
    public void Matcher_SurrogatePairNotSplit()
    {
        // The matcher's pending point falls between the two halves of an emoji.
        const string chunk = "ab\U0001F600cd";
        var redactor = new StreamRedactor([], [new ScriptedMatcher(4) { Pending = w => w.IndexOf('\uDE00') }]);

        var emitted = redactor.ProcessChunk(chunk);

        Assert.Equal("ab", emitted);
        Assert.Equal("\U0001F600cd", redactor.Flush());
    }

    // ── Release ──────────────────────────────────────────────────────────────────────

    /// <summary>Holds every character (a multi-line vault secret could start anywhere).</summary>
    private static IStreamMatcher HoldAll(params (string Find, string Label)[] finds) =>
        new ScriptedMatcher(5000)
        {
            Pending = _ => 0,
            Matches = w =>
            {
                List<StreamMatch> found = [];
                new LiteralMatcher(finds).FindMatches(w, found);
                return found;
            },
        };

    [Fact]
    public void Release_MasksCompleteMatchesInTail()
    {
        var redactor = new StreamRedactor([new StreamReplacement("plainsecret", "[P]")], [HoldAll(("fpsecret", "[M]"))]);

        Assert.Equal("", redactor.ProcessChunk("a plainsecret b fpsecret Password: "));
        Assert.Equal("a [P] b [M] Password: ", redactor.Release());
    }

    [Fact]
    public void Release_PartialPrefixReleasedUnmasked()
    {
        // The residual limit: only complete occurrences are recognised, so a prefix is released as text.
        var redactor = new StreamRedactor([new StreamReplacement("hunter2", "[P]")], [HoldAll()]);

        redactor.ProcessChunk("prompt> hunt");
        Assert.Equal("prompt> hunt", redactor.Release());
    }

    [Fact]
    public void Release_ThenNextChunk_Continues()
    {
        var redactor = new StreamRedactor([], [HoldAll(("secret", "[M]"))]);

        redactor.ProcessChunk("first secret ");
        Assert.Equal("first [M] ", redactor.Release());
        Assert.False(redactor.HasHeld);
        Assert.Equal("", redactor.ProcessChunk("then secret"));
        Assert.Equal("then [M]", redactor.Flush());
    }

    [Fact]
    public void Release_EmptyTail_ReturnsEmpty()
    {
        var redactor = new StreamRedactor([], [Inert()]);

        Assert.Equal("", redactor.Release());
        Assert.Equal("x", redactor.ProcessChunk("x"));
        Assert.Equal("", redactor.Release());
    }

    [Fact]
    public void Flush_AfterRelease()
    {
        var redactor = new StreamRedactor([], [HoldAll(("secret", "[M]"))]);

        redactor.ProcessChunk("one ");
        Assert.Equal("one ", redactor.Release());
        redactor.ProcessChunk("secret two");
        Assert.Equal("[M] two", redactor.Flush());
        Assert.Equal("", redactor.Flush());
    }

    [Fact]
    public void HasHeld_TracksTail()
    {
        var redactor = new StreamRedactor([], [HoldAll()]);
        Assert.False(redactor.HasHeld);

        redactor.ProcessChunk("held");
        Assert.True(redactor.HasHeld);

        redactor.Release();
        Assert.False(redactor.HasHeld);

        redactor.ProcessChunk("again");
        Assert.True(redactor.HasHeld);
        redactor.Flush();
        Assert.False(redactor.HasHeld);
    }

    [Fact]
    public void HasHeld_AndRelease_OnTheOneDotZeroPath()
    {
        var redactor = new StreamRedactor([new StreamReplacement("hunter2", "[P]")]);

        Assert.Equal("x", redactor.ProcessChunk("xhunter2"));
        Assert.True(redactor.HasHeld);
        Assert.Equal("[P]", redactor.Release());
        Assert.False(redactor.HasHeld);
    }

    // ── no matchers: the 1.0 path ────────────────────────────────────────────────────

    [Fact]
    public void NoMatchers_SameAsOneDotZero()
    {
        var rng = new Random(103);
        for (var round = 0; round < 500; round++)
        {
            List<StreamReplacement> replacements = [];
            for (var f = rng.Next(0, 5); f > 0; f--)
                replacements.Add(new StreamReplacement(RandomText(rng, rng.Next(1, 6), "abc"), $"[{f}]"));
            var text = RandomText(rng, rng.Next(0, 60), "abc d");
            List<string> chunks = [];
            for (var i = 0; i < text.Length;)
            {
                var size = rng.Next(1, 10);
                chunks.Add(text.Substring(i, Math.Min(size, text.Length - i)));
                i += size;
            }

            var oneDotZero = Pieces(new StreamRedactor(replacements), chunks);
            var emptyMatchers = Pieces(new StreamRedactor(replacements, []), chunks);

            Assert.Equal(oneDotZero, emptyMatchers);
        }
    }
}

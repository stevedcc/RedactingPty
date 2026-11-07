using System.Text;
using Xunit;

namespace RedactingPty.Tests;

/// <summary>
/// The <see cref="RedactionOptions"/> overload on every runner: the test host's literal matcher
/// (<c>-m FIND LABEL</c>) masks what the child prints, and a matcher failure fails closed.
/// </summary>
public partial class PtyRunnerTests
{
    private const string MatcherSecret = "fp-only-s3cret";
    private const string MatcherLabel = "[MATCHED]";
    private static readonly string[] MatchSecret = ["-m", MatcherSecret, MatcherLabel];

    [PtyFact]
    public void Run_WithMatcher_MasksChildOutput()
    {
        var r = RunHost(MatchSecret, OperatingSystem.IsWindows()
            ? ["cmd", "/c", $"echo before {MatcherSecret} after"]
            : ["sh", "-c", $"echo before {MatcherSecret} after"]);

        r.AssertSecretAbsent(MatcherSecret);
        Assert.Contains($"before {MatcherLabel} after\n", r.Text);
        Assert.Equal(0, r.ExitCode);
    }

    [UnixPtyFact]
    public void Fallback_Run_WithMatcher_MasksChildOutput()
    {
        var r = RunHost(MatchSecret, ["sh", "-c", $"echo before {MatcherSecret} after"], runner: "fallback");

        r.AssertSecretAbsent(MatcherSecret);
        Assert.Contains($"before {MatcherLabel} after\n", r.Text);
        Assert.Equal(0, r.ExitCode);
    }

    [UnixPtyFact]
    public void Fallback_OneMatcher_StdoutAndStderr_BothMasked()
    {
        // The host's stdout is redirected, so FallbackPty writes the child's stderr there too.
        var r = RunHost(MatchSecret, ["sh", "-c", $"echo out-{MatcherSecret}; echo err-{MatcherSecret} 1>&2"], runner: "fallback");

        // Each stream's redactor holds its own tail, so the two lines may interleave: check each
        // stream's text and that both occurrences were masked, not the line layout.
        r.AssertSecretAbsent(MatcherSecret);
        Assert.Contains("out-", r.Text);
        Assert.Contains("err-", r.Text);
        Assert.Equal(2, CountOccurrences(r.Text, MatcherLabel));
        Assert.Equal(0, r.ExitCode);
    }

    public static TheoryData<string> UnixRunners => OperatingSystem.IsMacOS() ? ["macos", "fallback"] : ["linux", "fallback"];

    /// <summary>
    /// The matcher throws on its second call while the child keeps printing far more than a PTY or pipe
    /// buffer holds: nothing from the failing chunk on reaches the output, only the marker; the relay
    /// keeps draining (the child is reaped, the host exits) and Run rethrows a message with no output text.
    /// </summary>
    [UnixPtyTheory]
    [MemberData(nameof(UnixRunners))]
    public void Run_MatcherThrows_NothingAfterFailure_MessageHasNoText(string runner)
    {
        // The 20-character find makes the redactor hold 19 characters of the first line when the
        // matcher throws on the next chunk: that held tail must be discarded too.
        using var session = HostSession.Start(HostPath, runner, ["--throw-on-call", "2", "-m", "never-printed-find-x", "[M]"],
            ["sh", "-c", $"printf 'first-chunk{new string('X', 40)}\\n'; read x; i=0; while [ $i -lt 20000 ]; do echo after-$i; i=$((i+1)); done"]);

        // 40 X and the line ending (\r\n through a PTY, \n through a pipe), less the 19 held characters.
        var emittedX = 40 - 19 + (runner == "fallback" ? 1 : 2);
        session.WaitForOutput(o => o.Contains("first-chunk" + new string('X', emittedX)), "the first chunk");
        session.Send("go\n");
        var (exitCode, output, stderr) = session.WaitForExit();

        Assert.Equal(120, exitCode); // the host's "Run threw"
        Assert.Contains("[output truncated: redaction failed]", output);
        Assert.DoesNotContain("after-", output);
        Assert.DoesNotContain(new string('X', emittedX + 1), output);
        Assert.Contains("test matcher failure", stderr);
        Assert.DoesNotContain("after-", stderr);
        Assert.DoesNotContain("first-chunk", stderr);
        // The marker is the last thing written.
        Assert.EndsWith("[output truncated: redaction failed]", output.TrimEnd('\r', '\n'));
    }

    // ── custom runners ───────────────────────────────────────────────────────────────

    private sealed class OneDotZeroRunner : IPtyRunner
    {
        public int Calls { get; private set; }

        public int Run(string[] argv, IReadOnlyList<StreamReplacement> replacements)
        {
            Calls++;
            return 42;
        }
    }

    private sealed class NoMatches : IStreamMatcher
    {
        public int MaxMatchLength => 1;
        public void FindMatches(ReadOnlySpan<char> window, ICollection<StreamMatch> matches) { }
    }

    [Fact]
    public void CustomRunner_DefaultOverload_EmptyDelegates()
    {
        var runner = new OneDotZeroRunner();

        Assert.Equal(42, ((IPtyRunner)runner).Run(["x"], [], new RedactionOptions()));
        Assert.Equal(1, runner.Calls);
    }

    [Fact]
    public void CustomRunner_DefaultOverload_WithMatchersThrows()
    {
        var runner = new OneDotZeroRunner();
        IPtyRunner asInterface = runner;

        Assert.Throws<NotSupportedException>(() => asInterface.Run(["x"], [], new RedactionOptions { Matchers = [new NoMatches()] }));
        Assert.Throws<NotSupportedException>(() => asInterface.Run(["x"], [], new RedactionOptions { ReleaseWhenAwaitingInput = true }));
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public void CustomRunner_DefaultOverload_NullOptions_ThrowsArgumentNull()
    {
        IPtyRunner runner = new OneDotZeroRunner();

        Assert.Throws<ArgumentNullException>(() => runner.Run(["x"], [], null!));
    }

    [Fact]
    public void Fallback_BothStreamsFail_BothReported()
    {
        var stdout = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(new InvalidOperationException("stdout failed"));
        var stderr = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(new InvalidOperationException("stderr failed"));

        var both = Assert.Throws<AggregateException>(() => FallbackPty.ThrowFailures(stdout, stderr));
        Assert.Equal(["stdout failed", "stderr failed"], both.InnerExceptions.Select(e => e.Message));

        Assert.Equal("stdout failed", Assert.Throws<InvalidOperationException>(() => FallbackPty.ThrowFailures(stdout, null)).Message);
        Assert.Equal("stderr failed", Assert.Throws<InvalidOperationException>(() => FallbackPty.ThrowFailures(null, stderr)).Message);
        FallbackPty.ThrowFailures(null, null); // nothing to report
    }

    // ── FallbackPty.Drain, in memory ─────────────────────────────────────────────────

    private sealed class ThrowOnSecondCall : IStreamMatcher
    {
        private int _calls;
        public int MaxMatchLength => 10; // the default PendingFrom holds 9 characters

        public void FindMatches(ReadOnlySpan<char> window, ICollection<StreamMatch> matches)
        {
            if (++_calls == 2)
                throw new InvalidOperationException("matcher failed");
        }
    }

    [Fact]
    public void Fallback_Drain_MatcherThrows_DiscardsTheRest_KeepsDraining()
    {
        var input = new string('a', 4096) + new string('b', 4096) + new string('c', 50_000);
        var source = new MemoryStream(Encoding.UTF8.GetBytes(input));
        var dest = new MemoryStream();
        var replacements = MatcherCarrier.For([], new RedactionOptions { Matchers = [new ThrowOnSecondCall()] });

        var failure = FallbackPty.Drain(source, dest, replacements, Encoding.UTF8, writeLock: null);

        var output = Encoding.UTF8.GetString(dest.ToArray());
        // The 9 held characters of the first chunk are discarded with the failing chunk.
        Assert.Equal(new string('a', 4096 - 9) + "[output truncated: redaction failed]" + Environment.NewLine, output);
        Assert.Equal(source.Length, source.Position);
        Assert.NotNull(failure);
        Assert.Equal("matcher failed", Assert.Throws<InvalidOperationException>(() => failure.Throw()).Message);
    }
}

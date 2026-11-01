using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace RedactingPty.Tests;

/// <summary>
/// Characterization tests for the real PTY runners (<see cref="LinuxPty"/>, <see cref="MacOSPty"/>,
/// <see cref="WindowsPty"/>): each test runs a real child process in a pseudo-terminal through
/// RedactingPty.TestHost and asserts what a user sees, the redacted output and the exit code.
///
/// The host is launched with stdin, stdout and stderr redirected (stdin closed at once), so
/// the tests reach the runners only through the host, which constructs them directly.
/// Assertions are robust to terminal framing: the secret must be absent from the raw output
/// bytes; then ANSI CSI/OSC sequences are stripped, <c>\r\n</c> becomes <c>\n</c>, and the
/// expected text is checked with <c>Contains</c>.
/// </summary>
public partial class PtyRunnerTests
{
    private const string Secret = "hunter2";
    private const string Marker = "[REDACTED]";
    private static readonly string[] Redact = ["-r", Secret, Marker];

    // ── all three OSes ───────────────────────────────────────────────────────────────

    [PtyFact]
    public void SecretPrintedByChild_IsReplaced_SurroundingTextUnchanged()
    {
        var r = RunHost(Redact, OperatingSystem.IsWindows()
            ? ["cmd", "/c", "echo before hunter2 after"]
            : ["sh", "-c", "echo before hunter2 after"]);

        r.AssertSecretAbsent(Secret);
        Assert.Contains("before [REDACTED] after\n", r.Text);
        Assert.Equal(0, r.ExitCode);
    }

    [PtyFact]
    public void ThreeHundredLinesOfBackToBackSecrets_NoLeak_ExactlyOneReplacementPerOccurrence()
    {
        // 300 lines of four back-to-back secrets (30 columns each): nearly every byte offset
        // lies inside an occurrence, so the runners' 4096-byte read boundaries land inside
        // secrets. Every occurrence must be replaced and none may leak.
        const string line = "hunter2hunter2hunter2hunter2 .";
        var r = RunHost(Redact, OperatingSystem.IsWindows()
            ? ["cmd", "/c", $"for /L %i in (1,1,300) do @echo {line}"]
            : ["sh", "-c", $"i=0; while [ $i -lt 300 ]; do echo '{line}'; i=$((i+1)); done"]);

        r.AssertSecretAbsent(Secret);
        Assert.Equal(1200, CountOccurrences(r.Text, Marker));
        Assert.Equal(0, r.ExitCode);
    }

    [PtyFact]
    public void SecretAsLastBytesWithoutTrailingNewline_IsReplaced()
    {
        // The secret sits in the redactor's held-back tail when the stream ends: only
        // StreamRedactor.Flush emits it.
        var r = RunHost(Redact, OperatingSystem.IsWindows()
            // set /p with empty input sets ERRORLEVEL 1, hence the explicit exit 0.
            ? ["cmd", "/c", "set /p =tail-hunter2<nul&exit 0"]
            : ["sh", "-c", "printf '%s' tail-hunter2"]);

        r.AssertSecretAbsent(Secret);
        Assert.Contains("tail-[REDACTED]", r.Text);
        if (!OperatingSystem.IsWindows())
            Assert.EndsWith("tail-[REDACTED]", r.Text); // ConPTY may append VT sequences after it
        Assert.Equal(0, r.ExitCode);
    }

    [PtyFact]
    public void ChildExitCode_PassesThrough()
    {
        var r = RunHost(Redact, OperatingSystem.IsWindows()
            ? ["cmd", "/c", "exit 7"]
            : ["sh", "-c", "exit 7"]);

        Assert.Equal(7, r.ExitCode);
    }

    // ── Unix only ────────────────────────────────────────────────────────────────────

    [UnixPtyFact]
    public void ChildKilledBySignal_Returns128PlusSignal()
    {
        var r = RunHost(Redact, ["sh", "-c", "kill -TERM $$"]);

        Assert.Equal(128 + 15, r.ExitCode);
    }

    [UnixPtyFact]
    public void Child_HasATerminal()
    {
        var r = RunHost(Redact, ["sh", "-c", "test -t 1 && echo is-a-tty"]);

        Assert.Contains("is-a-tty\n", r.Text);
        Assert.Equal(0, r.ExitCode);
    }

    [UnixPtyFact]
    public void ChildStderr_IsMergedAndRedacted()
    {
        var r = RunHost(Redact, ["sh", "-c", "echo on-stderr hunter2 end 1>&2"]);

        r.AssertSecretAbsent(Secret);
        Assert.Contains("on-stderr [REDACTED] end\n", r.Text);
        Assert.DoesNotContain(Secret, r.Stderr);
        Assert.Equal(0, r.ExitCode);
    }

    [UnixPtyFact]
    public void CommandThatDoesNotExist_Exits127_WithExecFailedDiagnostic()
    {
        var r = RunHost(Redact, ["redactingpty-test-no-such-command"]);

        Assert.Equal(127, r.ExitCode);
        Assert.Contains("redactingpty: exec failed (command not found or not executable)\n", r.Text);
    }

    [UnixPtyFact]
    public void SecretSplitAcrossTwoChildWrites_IsReplaced_BestEffort()
    {
        // Best effort: the child writes "<4000 filler>hun", pauses, then "ter2 tail", so the
        // secret is usually split across two reads. If the kernel coalesces the writes into
        // one read this still passes, so it is not the required overlap check; the 300-line
        // test is.
        var filler = new string('x', 4000);
        var r = RunHost(Redact,
            ["sh", "-c", $"printf '%s' '{filler}hun'; sleep 0.3; printf 'ter2 tail'"]);

        r.AssertSecretAbsent(Secret);
        Assert.Contains(filler + "[REDACTED] tail", r.Text);
        Assert.Equal(0, r.ExitCode);
    }

    // ── plumbing ─────────────────────────────────────────────────────────────────────

    private static readonly TimeSpan LaunchTimeout = TimeSpan.FromSeconds(30);

    private static string RunnerName =>
        OperatingSystem.IsLinux() ? "linux"
        : OperatingSystem.IsMacOS() ? "macos"
        : OperatingSystem.IsWindows() ? "windows"
        : throw new PlatformNotSupportedException("no PTY runner for this OS");

    /// <summary>
    /// Names the test host binary to launch, e.g. a NativeAOT-published one. Unset (or empty)
    /// means the JIT host built next to the tests.
    /// </summary>
    private const string HostPathVariable = "REDACTINGPTY_TESTHOST";

    private static string HostPath =>
        Environment.GetEnvironmentVariable(HostPathVariable) is { Length: > 0 } fromEnvironment
            ? Path.GetFullPath(fromEnvironment)
            : Path.Combine(AppContext.BaseDirectory,
                OperatingSystem.IsWindows() ? "RedactingPty.TestHost.exe" : "RedactingPty.TestHost");

    private static HostResult RunHost(string[] replacementArgs, string[] childArgv)
    {
        var hostPath = HostPath;
        Assert.True(File.Exists(hostPath), $"test host not found at {hostPath} ({HostPathVariable}: "
            + $"{Environment.GetEnvironmentVariable(HostPathVariable) ?? "unset"})");

        var psi = new ProcessStartInfo(hostPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add(RunnerName);
        foreach (var a in replacementArgs) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("--");
        foreach (var a in childArgv) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
        // Empty stdin: the runner's stdin-forwarding thread sees EOF and sends nothing the
        // PTY could echo back.
        process.StandardInput.Close();

        // Read stdout and stderr concurrently to EOF before waiting for exit, so neither
        // pipe can fill and deadlock the host.
        var stdout = new MemoryStream();
        var stdoutTask = process.StandardOutput.BaseStream.CopyToAsync(stdout);
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(LaunchTimeout);
        try
        {
            Task.WhenAll(stdoutTask, stderrTask).WaitAsync(timeout.Token).GetAwaiter().GetResult();
            process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            Assert.Fail($"test host did not finish within {LaunchTimeout.TotalSeconds} s; "
                + $"stdout so far: {Encoding.UTF8.GetString(stdout.ToArray())}; "
                + $"stderr so far: {(stderrTask.IsCompletedSuccessfully ? stderrTask.Result : "(incomplete)")}");
        }

        var result = new HostResult(process.ExitCode, stdout.ToArray(), stderrTask.Result);
        // 120: the runner threw (the host prints the exception); 121: host usage error.
        Assert.True(result.ExitCode is not (120 or 121),
            $"test host failed with exit code {result.ExitCode}: {result.Stderr}");
        return result;
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        for (int idx = text.IndexOf(value, StringComparison.Ordinal);
             idx >= 0;
             idx = text.IndexOf(value, idx + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    // CSI: ESC [ params intermediates final. OSC: ESC ] ... terminated by BEL or ST (ESC \).
    [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]|\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)")]
    private static partial Regex AnsiSequence();

    private sealed record HostResult(int ExitCode, byte[] RawStdout, string Stderr)
    {
        /// <summary>stdout decoded as UTF-8, ANSI CSI/OSC stripped, <c>\r\n</c> normalised to <c>\n</c>.</summary>
        public string Text { get; } =
            AnsiSequence().Replace(Encoding.UTF8.GetString(RawStdout), "").Replace("\r\n", "\n");

        /// <summary>The primary assertion: the secret's bytes appear nowhere in the raw output.</summary>
        public void AssertSecretAbsent(string secret)
        {
            var index = RawStdout.AsSpan().IndexOf(Encoding.UTF8.GetBytes(secret));
            Assert.True(index < 0,
                $"secret '{secret}' leaked at byte offset {index} of the raw output: {Encoding.UTF8.GetString(RawStdout)}");
        }
    }
}

/// <summary>
/// A <see cref="FactAttribute"/> that runs only where a real PTY runner exists: Linux, macOS,
/// and Windows 10 1809+ (ConPTY). xunit v2 has no runtime skip, so the skip is set here.
/// </summary>
public sealed class PtyFactAttribute : FactAttribute
{
    public PtyFactAttribute()
    {
        if (!(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()
              || OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)))
            Skip = "No real PTY runner on this OS.";
    }
}

/// <summary>A <see cref="FactAttribute"/> that runs only on Linux and macOS (the forkpty runners).</summary>
public sealed class UnixPtyFactAttribute : FactAttribute
{
    public UnixPtyFactAttribute()
    {
        if (!(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
            Skip = "Unix (forkpty) runners only.";
    }
}

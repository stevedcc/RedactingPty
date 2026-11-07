using System.Runtime.InteropServices;
using Xunit;

namespace RedactingPty.Tests;

/// <summary>
/// Releasing held output at a prompt, through a real PTY. The test host's <c>--hold-all</c> matcher
/// (PendingFrom 0, MaxMatchLength 5000) stands in for a multi-line vault secret: it holds every
/// character, so without a release nothing would appear before the child exits. Every test waits on a
/// concrete condition (text in the output, a number of checks in the check log, a <c>/proc</c> state);
/// the session's deadline only fails a test.
/// </summary>
public partial class PtyRunnerTests
{
    private const string Prompt = "Continue? [y/N] ";
    private const string BusyText = "held-before-sleep ";

    private sealed class CheckLog : IDisposable
    {
        private readonly string _folder = Directory.CreateTempSubdirectory("redactingpty-checks-").FullName;
        public string Path => System.IO.Path.Combine(_folder, "checks.log");
        public string[] Lines => File.Exists(Path) ? File.ReadAllLines(Path) : [];
        public void Dispose() => Directory.Delete(_folder, recursive: true);
    }

    private static string[] HoldAndRelease(CheckLog log) => ["--hold-all", "--release", "--check-log", log.Path];

    private static string[] TestChild(string mode) => [HostPath, "child", mode];

    // ── Linux and macOS ──────────────────────────────────────────────────────────────

    [LinuxPtyFact]
    public void RealPty_YesNoPrompt_ReachesOutput()
    {
        using var log = new CheckLog();
        using var session = HostSession.Start(HostPath, RunnerName, HoldAndRelease(log), TestChild("prompt"));

        session.WaitForOutput(o => o.Contains(Prompt), "the prompt, before any input is sent");
        Assert.Contains("true", log.Lines);

        session.Send("y\n");
        var (exitCode, output, _) = session.WaitForExit();
        Assert.Contains("answer: y", output);
        Assert.Equal(0, exitCode);
    }

    [UnixPtyFact]
    public void RealPty_PasswordPrompt_ReachesOutput()
    {
        using var log = new CheckLog();
        using var session = HostSession.Start(HostPath, RunnerName, HoldAndRelease(log),
            ["sh", "-c", "stty -echo; printf 'Password: '; read x; stty echo; echo got-$x"]);

        session.WaitForOutput(o => o.Contains("Password: "), "the password prompt, before any input is sent");

        session.Send("pw\n");
        var (exitCode, output, _) = session.WaitForExit();
        Assert.Contains("got-pw", output);
        Assert.Equal(0, exitCode);
    }

    /// <summary>
    /// As getpass does: the prompt read on a descriptor other than 0 opened on /dev/tty, echo off.
    /// /dev/tty's device numbers are (5, 0), not the slave's, so a rule that compares descriptors with
    /// the slave alone misses it.
    /// </summary>
    [UnixPtyFact]
    public void RealPty_GetpassOnDevTty_ReachesOutput()
    {
        using var log = new CheckLog();
        using var session = HostSession.Start(HostPath, RunnerName, HoldAndRelease(log),
            ["bash", "-c", "exec 5<>/dev/tty; stty -echo <&5; printf 'Passphrase: '; read -r -u 5 x; stty echo <&5; echo got-$x"]);

        session.WaitForOutput(o => o.Contains("Passphrase: "), "the passphrase prompt, before any input is sent");

        session.Send("pp\n");
        var (exitCode, output, _) = session.WaitForExit();
        Assert.Contains("got-pp", output);
        Assert.Equal(0, exitCode);
    }

    // ── Linux only ───────────────────────────────────────────────────────────────────

    [LinuxPtyFact]
    public void RealPty_BusyChildPaused_TailHeld()
    {
        using var log = new CheckLog();
        using var session = HostSession.Start(HostPath, RunnerName, HoldAndRelease(log), TestChild("busy"));

        HostSession.WaitFor(() => DescendantThreads(session.HostPid).Any(t => SyscallNumber(t) == ClockNanosleep),
            "the child in clock_nanosleep");
        HostSession.WaitFor(() => log.Lines.Length >= 3, "three input-wait checks");

        Assert.DoesNotContain(BusyText, session.Output);
        Assert.All(log.Lines, line => Assert.Equal("false", line));
    }

    [LinuxPtyFact]
    public void RealPty_TwoThreads_OneReadsTtyOneRuns_Held()
    {
        using var log = new CheckLog();
        using var session = HostSession.Start(HostPath, RunnerName, HoldAndRelease(log), TestChild("two-threads"));

        HostSession.WaitFor(() => DescendantThreads(session.HostPid).Any(t => t.StartsWith($"{ReadSyscall} 0x0 ")),
            "a child thread blocked in read(0)");
        HostSession.WaitFor(() => log.Lines.Length >= 3, "three input-wait checks");

        Assert.DoesNotContain(Prompt, session.Output);
        Assert.All(log.Lines, line => Assert.Equal("false", line));

        session.Send("n\n");
        var (exitCode, output, _) = session.WaitForExit();
        Assert.Contains(Prompt, output); // flushed at exit
        Assert.Contains("answer: n", output);
        Assert.Equal(0, exitCode);
    }

    // ── macOS only ───────────────────────────────────────────────────────────────────

    /// <summary>The stated limit: a y/N prompt keeps echo on, so macOS holds it until more output or exit.</summary>
    [MacPtyFact]
    public void RealPty_MacOS_YesNoPrompt_StaysHeld()
    {
        using var log = new CheckLog();
        using var session = HostSession.Start(HostPath, RunnerName, HoldAndRelease(log), TestChild("prompt"));

        HostSession.WaitFor(() => log.Lines.Length >= 3, "three input-wait checks");
        Assert.DoesNotContain(Prompt, session.Output);
        Assert.All(log.Lines, line => Assert.Equal("false", line));

        session.Send("y\n");
        var (exitCode, output, _) = session.WaitForExit();
        Assert.Contains(Prompt, output);
        Assert.Equal(0, exitCode);
    }

    /// <summary>
    /// macOS, raw mode: echo off but canonical mode off too (as vim or less run), so the probe, which reads
    /// the slave's flags, holds. The password-prompt case (echo off, canonical on: released) is
    /// <see cref="RealPty_PasswordPrompt_ReachesOutput"/>, which runs on macOS too.
    /// </summary>
    [MacPtyFact]
    public void RealPty_MacOS_RawModeNoEcho_StaysHeld()
    {
        using var log = new CheckLog();
        using var session = HostSession.Start(HostPath, RunnerName, HoldAndRelease(log),
            ["sh", "-c", "stty raw -echo; printf 'RAW> '; dd bs=1 count=1 >/dev/null 2>&1; stty sane; echo done-raw"]);

        HostSession.WaitFor(() => log.Lines.Length >= 3, "three input-wait checks");
        Assert.DoesNotContain("RAW> ", session.Output);
        Assert.All(log.Lines, line => Assert.Equal("false", line));

        session.Send("x");
        var (exitCode, output, _) = session.WaitForExit();
        Assert.Contains("RAW> ", output);
        Assert.Contains("done-raw", output);
        Assert.Equal(0, exitCode);
    }

    // ── Windows only ─────────────────────────────────────────────────────────────────

    /// <summary>ConPTY has no probe: held text waits for exit, and no check is ever made.</summary>
    [WindowsPtyFact]
    public void WindowsPty_NoProbe_HeldUntilExit()
    {
        using var log = new CheckLog();
        using var session = HostSession.Start(HostPath, RunnerName, HoldAndRelease(log),
            ["cmd", "/c", "set /p x=Continue? & echo answer-%x%"]);

        session.Send("y\r\n");
        var (exitCode, output, _) = session.WaitForExit();

        Assert.Contains("Continue?", output);
        Assert.Contains("answer-y", output);
        Assert.Empty(log.Lines);
        Assert.Equal(0, exitCode);
    }

    // ── /proc helpers (Linux) ────────────────────────────────────────────────────────

    private static int ReadSyscall => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 63 : 0;
    private static int ClockNanosleep => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 115 : 230;

    private static int? SyscallNumber(string line) =>
        int.TryParse(line.Split(' ')[0], out var nr) ? nr : null;

    /// <summary>The syscall lines of every thread of every descendant of <paramref name="pid"/>.</summary>
    private static List<string> DescendantThreads(int pid)
    {
        Dictionary<int, int> parents = [];
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var p)) continue;
            try
            {
                var stat = File.ReadAllText(Path.Combine(dir, "stat"));
                var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
                parents[p] = int.Parse(fields[1]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException) { } // exited while listed, or not ours
        }

        List<string> lines = [];
        foreach (var p in parents.Keys.Where(p => IsDescendant(p, pid, parents)))
        {
            try
            {
                foreach (var task in Directory.EnumerateDirectories($"/proc/{p}/task"))
                    lines.Add(File.ReadAllText(Path.Combine(task, "syscall")).Trim());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // exited while listed, or not ours
        }
        return lines;
    }

    private static bool IsDescendant(int p, int ancestor, Dictionary<int, int> parents)
    {
        for (var hops = 0; hops < 64 && parents.TryGetValue(p, out var parent); hops++, p = parent)
            if (parent == ancestor) return true;
        return false;
    }
}

/// <summary>A <see cref="FactAttribute"/> that runs only on Linux.</summary>
public sealed class LinuxPtyFactAttribute : FactAttribute
{
    public LinuxPtyFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
            Skip = "Linux only.";
    }
}

/// <summary>A <see cref="FactAttribute"/> that runs only on macOS.</summary>
public sealed class MacPtyFactAttribute : FactAttribute
{
    public MacPtyFactAttribute()
    {
        if (!OperatingSystem.IsMacOS())
            Skip = "macOS only.";
    }
}

/// <summary>A <see cref="FactAttribute"/> that runs only on Windows 10 1809+ (ConPTY).</summary>
public sealed class WindowsPtyFactAttribute : FactAttribute
{
    public WindowsPtyFactAttribute()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
            Skip = "Windows ConPTY only.";
    }
}

using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace RedactingPty.Tests;

/// <summary>
/// <see cref="RedactionOptions.Environment"/>: variables for the child only. The tests give them through
/// the test host's <c>--env-file</c> (so no value is in the host's own argv) and seed the host's own
/// environment for inherited variables. Every variable name carries a GUID, so nothing is inherited from
/// the machine or CI by accident.
/// </summary>
public partial class PtyRunnerTests
{
    /// <summary>A temporary folder for an environment file (and anything else a test writes); deleted on dispose.</summary>
    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("redactingpty-env-").FullName;

        /// <summary>Writes <paramref name="variables"/> as <c>NAME=VALUE</c> lines; returns the file's path.</summary>
        public string EnvFile(IReadOnlyDictionary<string, string> variables)
        {
            var file = System.IO.Path.Combine(Path, "child.env");
            File.WriteAllLines(file, variables.Select(v => $"{v.Key}={v.Value}"));
            return file;
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private static string NewName() => $"RPTY_{Guid.NewGuid():N}";

    private static string NewValue() => $"v{Guid.NewGuid():N}";

    // ── Linux and macOS (the forkpty runners) ─────────────────────────────────────────

    [UnixPtyFact]
    public void Environment_SetsVariableForTheChild()
    {
        using var temp = new TempFolder();
        var (name, value) = (NewName(), NewValue());

        var r = RunHost(["--env-file", temp.EnvFile(new Dictionary<string, string> { [name] = value }), "--report-own", name],
            ["sh", "-c", $"echo \"got-${name}\""]);

        Assert.Contains($"got-{value}\n", r.Text);
        Assert.Equal(0, r.ExitCode);
        // The host's own environment, managed and native, is unchanged after Run.
        Assert.Contains($"own-{name}=\n", r.Stderr);
        Assert.Contains($"own-native-{name}=\n", r.Stderr);
    }

    [UnixPtyFact]
    public void Environment_OverridesAnInheritedVariable()
    {
        using var temp = new TempFolder();
        var name = NewName();

        var r = RunHost(["--env-file", temp.EnvFile(new Dictionary<string, string> { [name] = "new" }), "--report-own", name],
            ["sh", "-c", $"echo \"got-${name}\""],
            hostEnvironment: new Dictionary<string, string> { [name] = "old" });

        Assert.Contains("got-new\n", r.Text);
        Assert.DoesNotContain("got-old", r.Text);
        Assert.Equal(0, r.ExitCode);
        Assert.Contains($"own-{name}=old\n", r.Stderr);
        Assert.Contains($"own-native-{name}=old\n", r.Stderr);
    }

    [UnixPtyFact]
    public void Environment_InheritsUnoverriddenVariables()
    {
        using var temp = new TempFolder();
        var (inherited, given) = (NewName(), NewName());

        var r = RunHost(["--env-file", temp.EnvFile(new Dictionary<string, string> { [given] = "given-value" })],
            ["sh", "-c", $"echo \"got-${inherited}-${given}\""],
            hostEnvironment: new Dictionary<string, string> { [inherited] = "inherited-value" });

        Assert.Contains("got-inherited-value-given-value\n", r.Text);
        Assert.Equal(0, r.ExitCode);
    }

    [UnixPtyFact]
    public void Environment_NullOrEmpty_InheritsUnchanged()
    {
        using var temp = new TempFolder();
        var name = NewName();
        var seed = new Dictionary<string, string> { [name] = "seeded-value" };
        string[] child = ["sh", "-c", $"echo \"got-${name}\""];

        var none = RunHost([], child, hostEnvironment: seed);
        var empty = RunHost(["--env-file", temp.EnvFile(new Dictionary<string, string>())], child, hostEnvironment: seed);

        Assert.Contains("got-seeded-value\n", none.Text);
        Assert.Equal(0, none.ExitCode);
        Assert.Contains("got-seeded-value\n", empty.Text);
        Assert.Equal(0, empty.ExitCode);
    }

    [LinuxPtyFact]
    public void Environment_ValueNotInTheChildsCommandLine()
    {
        using var temp = new TempFolder();
        var (name, value) = (NewName(), NewValue());

        using var session = HostSession.Start(HostPath, RunnerName,
            ["--env-file", temp.EnvFile(new Dictionary<string, string> { [name] = value })],
            ["sh", "-c", "echo \"pid=$$\"; read x"]);
        session.WaitForOutput(o => ChildPid().IsMatch(o), "the child's pid line");
        var pid = ChildPid().Match(session.Output).Groups[1].Value;

        var valueBytes = Encoding.UTF8.GetBytes(value);
        var cmdline = File.ReadAllBytes($"/proc/{pid}/cmdline");
        var environ = File.ReadAllBytes($"/proc/{pid}/environ");
        var hostCmdline = File.ReadAllBytes($"/proc/{session.HostPid}/cmdline");
        session.Send("done\n");
        var (exitCode, _, _) = session.WaitForExit();

        Assert.True(Encoding.UTF8.GetString(cmdline).Contains("pid=$$"), "the pid is not the sh child's");
        Assert.True(cmdline.AsSpan().IndexOf(valueBytes) < 0, "the value is in the child's /proc/<pid>/cmdline");
        Assert.True(hostCmdline.AsSpan().IndexOf(valueBytes) < 0, "the value is in the host's /proc/<pid>/cmdline");
        Assert.Contains($"{name}={value}", Encoding.UTF8.GetString(environ).Split('\0'));
        Assert.Equal(0, exitCode);
    }

    [GeneratedRegex(@"pid=(\d+)\r?\n")]
    private static partial Regex ChildPid();

    [UnixPtyFact]
    public void Environment_ValueStillRedactedInOutput()
    {
        using var temp = new TempFolder();
        var (name, value) = (NewName(), NewValue());

        var r = RunHost(["--env-file", temp.EnvFile(new Dictionary<string, string> { [name] = value }), "-r", value, Marker],
            ["sh", "-c", $"echo \"got-${name}\""]);

        r.AssertSecretAbsent(value);
        Assert.Contains($"got-{Marker}\n", r.Text);
        Assert.Equal(0, r.ExitCode);
    }

    [UnixPtyFact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void Environment_ChildPathIsSearched()
    {
        // The argv is the script's bare name: execvp must find it through the child's PATH (the only
        // folder that holds it). sh isn't on that PATH, so no shell does the search; the kernel runs
        // the shebang.
        using var temp = new TempFolder();
        var script = $"rpty-{Guid.NewGuid():N}";
        var marker = $"ran-{Guid.NewGuid():N}";
        var scriptPath = Path.Combine(temp.Path, script);
        File.WriteAllText(scriptPath, $"#!/bin/sh\necho {marker}\n");
        File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var r = RunHost(["--env-file", temp.EnvFile(new Dictionary<string, string> { ["PATH"] = temp.Path })], [script]);

        Assert.Contains($"{marker}\n", r.Text);
        Assert.Equal(0, r.ExitCode);
    }

    // ── FallbackPty ──────────────────────────────────────────────────────────────────

    [UnixPtyFact]
    public void Fallback_Environment_SetsVariableForTheChild()
    {
        using var temp = new TempFolder();
        var (name, value) = (NewName(), NewValue());

        var r = RunHost(["--env-file", temp.EnvFile(new Dictionary<string, string> { [name] = value }), "--report-own", name],
            ["sh", "-c", $"echo \"got-${name}\""], runner: "fallback");

        Assert.Contains($"got-{value}\n", r.Text);
        Assert.Equal(0, r.ExitCode);
        Assert.Contains($"own-{name}=\n", r.Stderr);
        Assert.Contains($"own-native-{name}=\n", r.Stderr);
    }

    [UnixPtyFact]
    public void Fallback_Environment_OverridesAnInheritedVariable()
    {
        using var temp = new TempFolder();
        var name = NewName();

        var r = RunHost(["--env-file", temp.EnvFile(new Dictionary<string, string> { [name] = "new" }), "--report-own", name],
            ["sh", "-c", $"echo \"got-${name}\""], runner: "fallback",
            hostEnvironment: new Dictionary<string, string> { [name] = "old" });

        Assert.Contains("got-new\n", r.Text);
        Assert.DoesNotContain("got-old", r.Text);
        Assert.Equal(0, r.ExitCode);
        Assert.Contains($"own-{name}=old\n", r.Stderr);
    }

    [WindowsPtyFact]
    public void Fallback_Environment_SetsVariableForTheChild_Windows()
    {
        using var temp = new TempFolder();
        var (name, value) = (NewName(), NewValue());

        var r = RunHost(["--env-file", temp.EnvFile(new Dictionary<string, string> { [name] = value }), "--report-own", name],
            ["cmd", "/c", $"echo got-%{name}%"], runner: "fallback");

        Assert.Contains($"got-{value}\n", r.Text);
        Assert.Equal(0, r.ExitCode);
        Assert.Contains($"own-{name}=\n", r.Stderr);
    }

    // ── Windows (ConPTY) ─────────────────────────────────────────────────────────────

    [WindowsPtyFact]
    public void Windows_Environment_SetsVariableForTheChild()
    {
        using var temp = new TempFolder();
        var (name, value) = (NewName(), NewValue());

        var r = RunHost(["--env-file", temp.EnvFile(new Dictionary<string, string> { [name] = value }), "--report-own", name],
            ["cmd", "/c", $"echo got-%{name}%"]);

        Assert.Contains($"got-{value}\n", r.Text);
        Assert.Equal(0, r.ExitCode);
        Assert.Contains($"own-{name}=\n", r.Stderr);
    }

    [WindowsPtyFact]
    public void Windows_Environment_OverridesAnInheritedVariable()
    {
        using var temp = new TempFolder();
        var name = NewName();

        // Given in lower case: Windows names ignore case, so it replaces the inherited one.
        var r = RunHost(["--env-file", temp.EnvFile(new Dictionary<string, string> { [name.ToLowerInvariant()] = "new" }), "--report-own", name],
            ["cmd", "/c", $"echo got-%{name}%"],
            hostEnvironment: new Dictionary<string, string> { [name] = "old" });

        Assert.Contains("got-new\n", r.Text);
        Assert.DoesNotContain("got-old", r.Text);
        Assert.Equal(0, r.ExitCode);
        Assert.Contains($"own-{name}=old\n", r.Stderr);
    }

    [WindowsPtyFact]
    public void Windows_Environment_InheritsUnoverriddenVariables()
    {
        using var temp = new TempFolder();
        var (inherited, given) = (NewName(), NewName());

        var r = RunHost(["--env-file", temp.EnvFile(new Dictionary<string, string> { [given] = "given-value" })],
            ["cmd", "/c", $"echo got-%{inherited}%-%{given}%"],
            hostEnvironment: new Dictionary<string, string> { [inherited] = "inherited-value" });

        Assert.Contains("got-inherited-value-given-value\n", r.Text);
        Assert.Equal(0, r.ExitCode);
    }

    [WindowsPtyFact]
    public void Windows_Environment_NullOrEmpty_InheritsUnchanged()
    {
        using var temp = new TempFolder();
        var name = NewName();
        var seed = new Dictionary<string, string> { [name] = "seeded-value" };
        string[] child = ["cmd", "/c", $"echo got-%{name}%"];

        var none = RunHost([], child, hostEnvironment: seed);
        var empty = RunHost(["--env-file", temp.EnvFile(new Dictionary<string, string>())], child, hostEnvironment: seed);

        Assert.Contains("got-seeded-value\n", none.Text);
        Assert.Equal(0, none.ExitCode);
        Assert.Contains("got-seeded-value\n", empty.Text);
        Assert.Equal(0, empty.ExitCode);
    }

    [WindowsPtyFact]
    public void Windows_Environment_ValueStillRedactedInOutput()
    {
        using var temp = new TempFolder();
        var (name, value) = (NewName(), NewValue());

        var r = RunHost(["--env-file", temp.EnvFile(new Dictionary<string, string> { [name] = value }), "-r", value, Marker],
            ["cmd", "/c", $"echo got-%{name}%"]);

        r.AssertSecretAbsent(value);
        Assert.Contains($"got-{Marker}\n", r.Text);
        Assert.Equal(0, r.ExitCode);
    }

    // ── the Windows environment block, in process (any OS) ───────────────────────────

    private static string Block(IReadOnlyDictionary<string, string> inherited, IReadOnlyDictionary<string, string> given) =>
        new(ChildEnvironment.BuildWindowsBlock(inherited, given));

    [Fact]
    public void WindowsBlock_SortedByNameIgnoringCase()
    {
        var block = Block(new Dictionary<string, string> { ["b"] = "2", ["A"] = "1", ["c"] = "3" },
            new Dictionary<string, string> { ["B2"] = "x", ["a1"] = "y" });

        // Ordinal order would put the upper-case names first (A, B2, a1, b, c).
        Assert.Equal("A=1\0a1=y\0b=2\0B2=x\0c=3\0\0", block);
    }

    [Fact]
    public void WindowsBlock_GivenNameReplacesInheritedIgnoringCase()
    {
        var block = Block(new Dictionary<string, string> { ["Path"] = @"C:\old", ["X"] = "1" },
            new Dictionary<string, string> { ["PATH"] = @"C:\new" });

        Assert.Equal("PATH=C:\\new\0X=1\0\0", block);
    }

    [Fact]
    public void WindowsBlock_EntriesAreNameEqualsValueNul_EndsWithTwoNuls()
    {
        var block = ChildEnvironment.BuildWindowsBlock(new Dictionary<string, string> { ["A"] = "" },
            new Dictionary<string, string> { ["B"] = "v=w" });

        Assert.Equal(['A', '=', '\0', 'B', '=', 'v', '=', 'w', '\0', '\0'], block);
    }

    // ── validation, in process ───────────────────────────────────────────────────────

    /// <summary>
    /// A dictionary that can hold what <see cref="Dictionary{TKey, TValue}"/> can't (a null key), for the
    /// validation tests. Lookups are linear.
    /// </summary>
    private sealed class RawEntries(params KeyValuePair<string, string>[] entries) : IReadOnlyDictionary<string, string>
    {
        public string this[string key] => entries.First(e => e.Key == key).Value;
        public IEnumerable<string> Keys => entries.Select(e => e.Key);
        public IEnumerable<string> Values => entries.Select(e => e.Value);
        public int Count => entries.Length;
        public bool ContainsKey(string key) => entries.Any(e => e.Key == key);
        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => ((IEnumerable<KeyValuePair<string, string>>)entries).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public bool TryGetValue(string key, [MaybeNullWhen(false)] out string value)
        {
            foreach (var e in entries)
            {
                if (e.Key == key) { value = e.Value; return true; }
            }
            value = null;
            return false;
        }
    }

    public static TheoryData<string> InvalidEntries =>
        ["null name", "empty name", "name with =", "name with NUL", "null value", "value with NUL"];

    private static KeyValuePair<string, string> InvalidEntry(string which) => which switch
    {
        "null name"      => new(null!, "v"),
        "empty name"     => new("", "v"),
        "name with ="    => new("A=B", "v"),
        "name with NUL"  => new("A\0B", "v"),
        "null value"     => new("RPTY_NULL_VALUE", null!),
        "value with NUL" => new("RPTY_NUL_VALUE", "a\0b"),
        _ => throw new ArgumentOutOfRangeException(nameof(which)),
    };

    /// <summary>Every built-in runner on this OS, FallbackPty included.</summary>
    private static IEnumerable<IPtyRunner> BuiltInRunners()
    {
        if (OperatingSystem.IsLinux()) yield return new LinuxPty();
        if (OperatingSystem.IsMacOS()) yield return new MacOSPty();
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) yield return new WindowsPty();
        yield return new FallbackPty();
    }

    /// <summary>A command that creates <paramref name="marker"/> if it runs.</summary>
    private static string[] CreatesMarker(string marker) => OperatingSystem.IsWindows()
        ? ["cmd", "/c", $"type nul > \"{marker}\""]
        : ["sh", "-c", $"touch '{marker}'"];

    [Theory]
    [MemberData(nameof(InvalidEntries))]
    public void Environment_InvalidEntry_ThrowsArgumentException_NothingStarted(string which)
    {
        using var temp = new TempFolder();
        foreach (var runner in BuiltInRunners())
        {
            var marker = Path.Combine(temp.Path, $"ran-{runner.GetType().Name}");
            var options = new RedactionOptions { Environment = new RawEntries(new("RPTY_VALID", "ok"), InvalidEntry(which)) };

            Assert.Throws<ArgumentException>(() => runner.Run(CreatesMarker(marker), [], options));
            Assert.False(File.Exists(marker), $"{runner.GetType().Name} started the child ({which})");
        }
    }

    [WindowsPtyFact]
    public void Environment_NamesEqualIgnoringCase_ThrowsOnWindows_NothingStarted()
    {
        using var temp = new TempFolder();
        foreach (var runner in BuiltInRunners())
        {
            var marker = Path.Combine(temp.Path, $"ran-{runner.GetType().Name}");
            var options = new RedactionOptions { Environment = new Dictionary<string, string> { ["Path"] = "a", ["PATH"] = "b" } };

            Assert.Throws<ArgumentException>(() => runner.Run(CreatesMarker(marker), [], options));
            Assert.False(File.Exists(marker), $"{runner.GetType().Name} started the child");
        }
    }
}

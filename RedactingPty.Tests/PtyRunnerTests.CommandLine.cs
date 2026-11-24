using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Xunit;

namespace RedactingPty.Tests;

/// <summary>
/// The command line a Windows child gets from each Windows runner, recorded by the test host's
/// <c>child cmdline FILE</c> mode (<c>GetCommandLineW()</c>, written to a file so ConPTY's rendering can't
/// change it), and <see cref="RedactionOptions.WindowsArguments"/>. The Windows tests run on CI only
/// (JIT Debug, JIT Release and NativeAOT hosts).
/// </summary>
public partial class PtyRunnerTests
{
    [LibraryImport("shell32.dll", EntryPoint = "CommandLineToArgvW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CommandLineToArgvW(string lpCmdLine, out int pNumArgs);

    [LibraryImport("kernel32.dll", EntryPoint = "LocalFree")]
    private static partial nint LocalFree(nint hMem);

    /// <summary>The C runtime's parse of <paramref name="commandLine"/> (shell32 <c>CommandLineToArgvW</c>).</summary>
    private static string[] ParseWindowsCommandLine(string commandLine)
    {
        var argvPtr = CommandLineToArgvW(commandLine, out var count);
        Assert.True(argvPtr != 0, $"CommandLineToArgvW failed (Win32 error {Marshal.GetLastPInvokeError()})");
        try
        {
            var parsed = new string[count];
            for (var k = 0; k < count; k++)
                parsed[k] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argvPtr, k * IntPtr.Size))!;
            return parsed;
        }
        finally
        {
            LocalFree(argvPtr);
        }
    }

    /// <summary>
    /// Splits a recorded command line into its program token (quoted or not) and the tail (which starts
    /// with the space after the token, or is empty).
    /// </summary>
    private static (string Token, string Tail) SplitProgramToken(string commandLine)
    {
        var end = commandLine.StartsWith('"') ? commandLine.IndexOf('"', 1) + 1 : commandLine.IndexOf(' ');
        if (end <= 0)
            end = commandLine.Length;
        return (commandLine[..end], commandLine[end..]);
    }

    /// <summary>Runs the host's <c>child cmdline</c> under <paramref name="runner"/>; returns the recorded command line.</summary>
    private static string RecordedCommandLine(string runner, string file, string[] args)
    {
        var r = RunHost([], [HostPath, "child", "cmdline", file, .. args], runner: runner);
        Assert.Equal(0, r.ExitCode);
        return Encoding.UTF8.GetString(File.ReadAllBytes(file));
    }

    [WindowsPtyFact]
    public void Windows_BothRunnersBuildTheSameCommandLine_CRuntimeParsesItBack()
    {
        using var temp = new TempFolder();
        List<string[]> runs = [.. WindowsCommandLineTests.WhiteSpaceChars.Select(c => new[] { "a" + c + "b" }), ["x y", "a\"b", "a b\\", ""]];

        foreach (var (args, n) in runs.Select((a, n) => (a, n)))
        {
            var file = Path.Combine(temp.Path, $"cmdline-{n}.txt");
            var lines = new Dictionary<string, string>();
            foreach (var runner in new[] { "windows", "fallback" })
            {
                File.Delete(file); // each run records into a fresh file
                var line = RecordedCommandLine(runner, file, args);
                Assert.Equal(["child", "cmdline", file, .. args], ParseWindowsCommandLine(line)[1..]);
                lines[runner] = line;
            }
            Assert.Equal(SplitProgramToken(lines["fallback"]).Tail, SplitProgramToken(lines["windows"]).Tail);
        }
    }

    // ── RedactionOptions.WindowsArguments ───────────────────────────────────────────

    /// <summary>Writes <paramref name="text"/> as UTF-8 without a BOM to a new file; returns its path.</summary>
    private static string WindowsArgumentsFile(TempFolder temp, string name, string text)
    {
        var file = Path.Combine(temp.Path, name);
        File.WriteAllBytes(file, Encoding.UTF8.GetBytes(text));
        return file;
    }

    [WindowsPtyFact]
    public void WindowsArguments_PassedVerbatimAfterTheProgramToken()
    {
        using var temp = new TempFolder();
        foreach (var runner in new[] { "windows", "fallback" })
        {
            // In quotes, so a temp path with a space still works (a temp path never holds '"').
            var file = Path.Combine(temp.Path, $"cmdline-{runner}.txt");
            var text = $"child cmdline \"{file}\" \"a b\" plain \"q\\\"x\" \"line\nbreak\"";

            var r = RunHost(["--windows-arguments-file", WindowsArgumentsFile(temp, $"args-{runner}.txt", text)], [HostPath], runner: runner);

            Assert.Equal(0, r.ExitCode);
            var recorded = File.ReadAllBytes(file);
            var token = Encoding.UTF8.GetString(recorded).StartsWith('"') ? $"\"{HostPath}\"" : HostPath;
            Assert.Equal(Encoding.UTF8.GetBytes($"{token} {text}"), recorded);
        }
    }

    [WindowsPtyFact]
    public void WindowsArguments_Empty_ProgramTokenAlone()
    {
        // The host with no arguments exits 121 (usage): the program token alone reached the child.
        // Through HostSession, not RunHost (which fails on 120 and 121).
        using var temp = new TempFolder();
        foreach (var runner in new[] { "windows", "fallback" })
        {
            using var session = HostSession.Start(HostPath, runner,
                ["--windows-arguments-file", WindowsArgumentsFile(temp, $"empty-{runner}.txt", "")], [HostPath]);
            session.CloseInput();
            var (exitCode, _, stderr) = session.WaitForExit();

            Assert.Equal(121, exitCode);
            // The outer host neither had a usage error nor threw: the 121 is the child's.
            Assert.DoesNotContain("redactingpty-testhost:", stderr);
        }
    }

    [WindowsPtyFact]
    public void WindowsArguments_WithArgvTailOrNul_ThrowsArgumentException_NothingStarted()
    {
        using var temp = new TempFolder();
        foreach (var runner in BuiltInRunners())
        {
            var name = runner.GetType().Name;
            var tailMarker = Path.Combine(temp.Path, $"ran-tail-{name}");
            var nulMarker = Path.Combine(temp.Path, $"ran-nul-{name}");

            Assert.Throws<ArgumentException>(() => runner.Run(["cmd", "/c", $"type nul > \"{tailMarker}\""], [],
                new RedactionOptions { WindowsArguments = "x" }));
            Assert.Throws<ArgumentException>(() => runner.Run(["cmd"], [],
                new RedactionOptions { WindowsArguments = $"/c type nul > \"{nulMarker}\"\0" }));

            Assert.False(File.Exists(tailMarker), $"{name} started the child (argv tail)");
            Assert.False(File.Exists(nulMarker), $"{name} started the child (NUL)");
        }
    }

    [UnixPtyFact]
    [UnsupportedOSPlatform("windows")]
    public void WindowsArguments_NotOnWindows_ThrowsArgumentException_NothingStarted()
    {
        // A one-element argv (an executable script run by its full path), so only the OS check can throw.
        using var temp = new TempFolder();
        foreach (var runner in BuiltInRunners())
        {
            var name = runner.GetType().Name;
            var marker = Path.Combine(temp.Path, $"ran-{name}");
            var script = Path.Combine(temp.Path, $"creates-marker-{name}");
            File.WriteAllText(script, $"#!/bin/sh\ntouch '{marker}'\n");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            Assert.Throws<ArgumentException>(() => runner.Run([script], [], new RedactionOptions { WindowsArguments = "x" }));
            Assert.False(File.Exists(marker), $"{name} started the child");
        }
    }

    [Fact]
    public void WindowsArguments_NotInToString()
    {
        var withText = new RedactionOptions { WindowsArguments = "secret-marker-7Hq" }.ToString();
        var withNull = new RedactionOptions().ToString();

        Assert.False(withText.Contains("secret-marker-7Hq", StringComparison.Ordinal), "ToString shows the WindowsArguments text");
        Assert.Contains("WindowsArguments = <17 characters>", withText);
        Assert.Contains("WindowsArguments = null", withNull);
        foreach (var member in new[] { "Matchers = ", "ReleaseWhenAwaitingInput = ", "Environment = " })
            Assert.Contains(member, withText);
    }
}

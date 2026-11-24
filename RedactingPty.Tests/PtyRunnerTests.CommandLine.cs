using System.Runtime.InteropServices;
using System.Text;
using Xunit;

namespace RedactingPty.Tests;

/// <summary>
/// The command line a Windows child gets from each Windows runner, recorded by the test host's
/// <c>child cmdline FILE</c> mode (<c>GetCommandLineW()</c>, written to a file so ConPTY's rendering can't
/// change it). Windows only: CI runs these (JIT Debug, JIT Release and NativeAOT hosts).
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
}

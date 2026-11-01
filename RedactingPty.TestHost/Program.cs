using System.Text;
using RedactingPty;

// Usage: RedactingPty.TestHost <linux|macos|windows> [-r FIND REPLACE]... -- ARGV...
//
// Runs ARGV through the named PTY runner, constructed directly (PtyRunnerFactory would pick
// FallbackPty because the tests redirect stdio), and exits with the child's exit code.
// If Run throws, prints the exception to stderr and exits with 120, a code no test child uses.
// Usage errors exit with 121.

const int RunThrew = 120;
const int UsageError = 121;

Console.OutputEncoding = Encoding.UTF8;

if (args.Length < 1)
    return Usage("missing runner name");

var replacements = new List<StreamReplacement>();
int i = 1;
for (; i < args.Length && args[i] != "--"; i++)
{
    if (args[i] != "-r" || i + 2 >= args.Length)
        return Usage($"unexpected argument '{args[i]}'");
    replacements.Add(new StreamReplacement(args[i + 1], args[i + 2]));
    i += 2;
}
if (i >= args.Length - 1)
    return Usage("missing '--' and child argv");
string[] argv = args[(i + 1)..];

IPtyRunner? runner = args[0] switch
{
    "linux" when OperatingSystem.IsLinux() => new LinuxPty(),
    "macos" when OperatingSystem.IsMacOS() => new MacOSPty(),
    "windows" when OperatingSystem.IsWindows() => new WindowsPty(),
    _ => null,
};
if (runner is null)
    return Usage($"runner '{args[0]}' is unknown or not supported on this OS");

try
{
    return runner.Run(argv, replacements);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"redactingpty-testhost: Run threw: {ex}");
    return RunThrew;
}

static int Usage(string problem)
{
    Console.Error.WriteLine($"redactingpty-testhost: {problem}");
    Console.Error.WriteLine("usage: RedactingPty.TestHost <linux|macos|windows> [-r FIND REPLACE]... -- ARGV...");
    return UsageError;
}

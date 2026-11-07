using System.Text;
using RedactingPty;
using RedactingPty.TestHost;

// Usage: RedactingPty.TestHost <linux|macos|windows|fallback> [OPTION]... -- ARGV...
//
// Runs ARGV through the named runner, constructed directly (PtyRunnerFactory would pick
// FallbackPty because the tests redirect stdio), and exits with the child's exit code.
// If Run throws, prints the exception to stderr and exits with 120, a code no test child uses.
// Usage errors exit with 121.
//
// Options:
//   -r FIND REPLACE    a plaintext replacement
//   -m FIND LABEL      a literal find for the test matcher
//   --throw-on-call N  the test matcher throws on its Nth FindMatches call
//   --hold-all         the test matcher holds every character (PendingFrom 0, MaxMatchLength 5000)
// Any -m, --throw-on-call or --hold-all runs the RedactionOptions overload with the test matcher.

const int RunThrew = 120;
const int UsageError = 121;

Console.OutputEncoding = Encoding.UTF8;

if (args.Length < 1)
    return Usage("missing runner name");

var replacements = new List<StreamReplacement>();
List<(string Find, string Label)> matcherFinds = [];
int? throwOnCall = null;
var holdAll = false;
var useOptions = false;
int i = 1;
for (; i < args.Length && args[i] != "--"; i++)
{
    switch (args[i])
    {
        case "-r" when i + 2 < args.Length:
            replacements.Add(new StreamReplacement(args[i + 1], args[i + 2]));
            i += 2;
            break;
        case "-m" when i + 2 < args.Length:
            matcherFinds.Add((args[i + 1], args[i + 2]));
            useOptions = true;
            i += 2;
            break;
        case "--throw-on-call" when i + 1 < args.Length && int.TryParse(args[i + 1], out var n):
            throwOnCall = n;
            useOptions = true;
            i++;
            break;
        case "--hold-all":
            holdAll = true;
            useOptions = true;
            break;
        default:
            return Usage($"unexpected argument '{args[i]}'");
    }
}
if (i >= args.Length - 1)
    return Usage("missing '--' and child argv");
string[] argv = args[(i + 1)..];

IPtyRunner? runner = args[0] switch
{
    "linux" when OperatingSystem.IsLinux() => new LinuxPty(),
    "macos" when OperatingSystem.IsMacOS() => new MacOSPty(),
    "windows" when OperatingSystem.IsWindows() => new WindowsPty(),
    "fallback" => new FallbackPty(),
    _ => null,
};
if (runner is null)
    return Usage($"runner '{args[0]}' is unknown or not supported on this OS");

try
{
    if (!useOptions)
        return runner.Run(argv, replacements);
    var options = new RedactionOptions { Matchers = [new TestMatcher(matcherFinds, throwOnCall, holdAll)] };
    return runner.Run(argv, replacements, options);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"redactingpty-testhost: Run threw: {ex}");
    return RunThrew;
}

static int Usage(string problem)
{
    Console.Error.WriteLine($"redactingpty-testhost: {problem}");
    Console.Error.WriteLine("usage: RedactingPty.TestHost <linux|macos|windows|fallback> [OPTION]... -- ARGV...");
    return UsageError;
}

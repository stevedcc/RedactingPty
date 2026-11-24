using System.Text;
using RedactingPty;
using RedactingPty.TestHost;

// Usage: RedactingPty.TestHost <linux|macos|windows|fallback> [OPTION]... -- ARGV...
//        RedactingPty.TestHost child <prompt|busy|two-threads>   (a test child, see ChildModes)
//        RedactingPty.TestHost child cmdline FILE [ARG]...        (Windows only: writes its own command
//                                                                   line to FILE, see ChildModes)
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
//   --release          RedactionOptions.ReleaseWhenAwaitingInput
//   --check-log FILE   append one line per input-wait check ("true" or "false") to FILE
//   --env-file FILE    RedactionOptions.Environment from FILE, one NAME=VALUE per line, split at the
//                      first '='; empty lines are skipped, an empty file is an empty dictionary. The
//                      values never go in this host's argv.
//   --report-own NAME  after Run returns, write own-NAME=<value> (System.Environment) and, on Unix,
//                      own-native-NAME=<value> (libc getenv) to stderr; empty when unset
//   --windows-arguments-file FILE
//                      RedactionOptions.WindowsArguments from FILE, read as UTF-8, the whole content
//                      unchanged (an empty file is the empty string). The text never goes in this
//                      host's argv.
// Any of -m, --throw-on-call, --hold-all, --release, --env-file, --report-own, --windows-arguments-file
// runs the RedactionOptions overload (with only --env-file, --report-own or --windows-arguments-file,
// Matchers is empty).

const int RunThrew = 120;
const int UsageError = 121;

if (args is ["child", var mode])
    return ChildModes.Run(mode);
if (args is ["child", "cmdline", var file, ..])
    return ChildModes.RecordCommandLine(file);

Console.OutputEncoding = Encoding.UTF8;

if (args.Length < 1)
    return Usage("missing runner name");

var replacements = new List<StreamReplacement>();
List<(string Find, string Label)> matcherFinds = [];
int? throwOnCall = null;
var holdAll = false;
var release = false;
string? checkLog = null;
Dictionary<string, string>? environment = null;
List<string> reportOwn = [];
string? windowsArguments = null;
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
        case "--release":
            release = true;
            useOptions = true;
            break;
        case "--check-log" when i + 1 < args.Length:
            checkLog = args[i + 1];
            i++;
            break;
        case "--env-file" when i + 1 < args.Length:
            environment = [];
            foreach (var line in File.ReadAllLines(args[i + 1]))
            {
                if (line.Length == 0)
                    continue;
                var eq = line.IndexOf('=');
                if (eq < 0)
                    return Usage("--env-file: a line with no '='");
                environment[line[..eq]] = line[(eq + 1)..];
            }
            useOptions = true;
            i++;
            break;
        case "--windows-arguments-file" when i + 1 < args.Length:
            windowsArguments = Encoding.UTF8.GetString(File.ReadAllBytes(args[i + 1]));
            useOptions = true;
            i++;
            break;
        case "--report-own" when i + 1 < args.Length:
            reportOwn.Add(args[i + 1]);
            useOptions = true;
            i++;
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
    // Only --env-file, --report-own and --windows-arguments-file: no matcher, as a caller that sets only
    // an environment or a command line.
    var onlyEnvironment = matcherFinds.Count == 0 && throwOnCall is null && !holdAll && !release;
    var options = new RedactionOptions
    {
        Matchers = onlyEnvironment ? [] : [new TestMatcher(matcherFinds, throwOnCall, holdAll)],
        ReleaseWhenAwaitingInput = release,
        InputWaitChecked = checkLog is null ? null : result => File.AppendAllText(checkLog, result ? "true\n" : "false\n"),
        Environment = environment,
        WindowsArguments = windowsArguments,
    };
    var exitCode = runner.Run(argv, replacements, options);
    foreach (var name in reportOwn)
    {
        Console.Error.WriteLine($"own-{name}={Environment.GetEnvironmentVariable(name)}");
        if (!OperatingSystem.IsWindows())
            Console.Error.WriteLine($"own-native-{name}={NativeEnvironment.Get(name)}");
    }
    return exitCode;
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

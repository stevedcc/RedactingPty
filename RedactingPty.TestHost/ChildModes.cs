using System.Runtime.InteropServices;
using System.Text;

namespace RedactingPty.TestHost;

/// <summary>
/// Test children for the input-wait release, run as <c>RedactingPty.TestHost child MODE</c> inside a
/// runner's PTY. They use libc's read and write on fd 0 and 1 directly (no Console, which would set up
/// the terminal), so what they block in is exactly what a prompt blocks in.
/// <list type="bullet">
/// <item><c>prompt</c>: writes <c>Continue? [y/N] </c>, reads a line from fd 0, writes <c>answer: LINE</c>.</item>
/// <item><c>busy</c>: writes <c>held-before-sleep </c>, then sleeps 30 s in <c>clock_nanosleep</c> (no input wait).</item>
/// <item><c>two-threads</c>: as <c>prompt</c>, while a second thread keeps running until the line is read.</item>
/// <item><c>cmdline FILE [ARG]...</c> (Windows only, see <see cref="RecordCommandLine"/>): writes the command
/// line this process was started with to <c>FILE</c>.</item>
/// </list>
/// </summary>
internal static partial class ChildModes
{
    public const string Prompt = "Continue? [y/N] ";
    public const string BusyText = "held-before-sleep ";

    [LibraryImport("libc", EntryPoint = "read", SetLastError = true)]
    private static partial nint read(int fd, [Out] byte[] buf, nint count);

    [LibraryImport("libc", EntryPoint = "write", SetLastError = true)]
    private static partial nint write(int fd, [In] byte[] buf, nint count);

    [LibraryImport("libc", EntryPoint = "sleep")]
    private static partial uint sleep(uint seconds);

    [LibraryImport("kernel32.dll", EntryPoint = "GetCommandLineW")]
    private static partial nint GetCommandLineW();

    /// <summary>
    /// Writes <c>GetCommandLineW()</c> to <paramref name="file"/> as UTF-8 without a BOM and returns 0;
    /// the other arguments are ignored. The file, not the PTY output, carries the result, so ConPTY's
    /// rendering can't change it. Not on Windows: a usage error (121).
    /// </summary>
    public static int RecordCommandLine(string file)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("redactingpty-testhost: child cmdline is Windows only");
            return 121;
        }
        File.WriteAllText(file, Marshal.PtrToStringUni(GetCommandLineW()), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return 0;
    }

    public static int Run(string mode)
    {
        switch (mode)
        {
            case "prompt":
                Write(Prompt);
                Write($"answer: {ReadLine()}\n");
                return 0;
            case "busy":
                Write(BusyText);
                sleep(30);
                return 0;
            case "two-threads":
                var stop = 0;
                var spinner = new Thread(() => { while (Volatile.Read(ref stop) == 0) { } }) { IsBackground = true };
                spinner.Start();
                Write(Prompt);
                var line = ReadLine();
                Volatile.Write(ref stop, 1);
                spinner.Join();
                Write($"answer: {line}\n");
                return 0;
            default:
                Console.Error.WriteLine($"redactingpty-testhost: unknown child mode '{mode}'");
                return 121;
        }
    }

    private static void Write(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        write(1, bytes, bytes.Length);
    }

    private static string ReadLine()
    {
        var line = new List<byte>();
        var one = new byte[1];
        while (read(0, one, 1) == 1 && one[0] != (byte)'\n')
            line.Add(one[0]);
        return Encoding.UTF8.GetString([.. line]).TrimEnd('\r');
    }
}

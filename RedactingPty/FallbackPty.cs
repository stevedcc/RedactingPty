using System.Diagnostics;
using System.Text;

namespace RedactingPty;

/// <summary>
/// Fallback runner for unsupported platforms or when stdout/stdin is redirected. Uses
/// <see cref="Process"/> with redirected streams; TTY semantics (colours, interactive
/// prompts) are not preserved.
///
/// Directly executes <c>argv[0]</c> with the remaining elements as its argument list
/// (no shell wrapper). <see cref="ProcessStartInfo.ArgumentList"/> handles platform-specific
/// quoting automatically.
///
/// Output is streamed through <see cref="StreamRedactor"/> using
/// <see cref="Console.OutputEncoding"/> for decoding and re-encoding (no line-ending
/// normalisation at the .NET stream level), giving downstream pipe consumers a complete,
/// redacted stream. Note: the decode/re-encode round-trip may alter bytes for sequences
/// that are invalid in the console encoding.
/// </summary>
internal sealed class FallbackPty : IPtyRunner
{
    public int Run(string[] argv, IReadOnlyList<StreamReplacement> replacements)
    {
        if (argv is not { Length: > 0 } || string.IsNullOrEmpty(argv[0]))
            throw new ArgumentException("argv must be non-empty and argv[0] must be a non-empty executable name.", nameof(argv));

        // Execute argv[0] directly (no shell wrapper). ProcessStartInfo.ArgumentList
        // handles per-platform quoting so each element is passed as a literal string.
        var startInfo = new ProcessStartInfo
        {
            FileName               = argv[0],
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };
        foreach (var a in argv.AsSpan(1))
            startInfo.ArgumentList.Add(a);

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdout   = Console.OpenStandardOutput();
        // When stdout is redirected (the only time FallbackPty is reached with a
        // redirected stream), both stdout and stderr should go to the same destination
        // so they are interleaved rather than diverging. When stdout is NOT redirected,
        // Console.OpenStandardError() returns the real terminal stderr stream, which is
        // independent — no shared lock is needed in that case.
        var stderr   = Console.IsOutputRedirected ? stdout : Console.OpenStandardError();
        var encoding = Console.OutputEncoding;

        // The lock is only needed when both tasks share the same Stream instance (stdout
        // redirected case above). When they target distinct streams the writes are
        // independent and no synchronisation is required.
        var sharedLock = Console.IsOutputRedirected ? new Lock() : null;

        // Drain stdout and stderr concurrently through StreamRedactor to avoid pipe-buffer
        // deadlocks when the child writes to both streams. Reading them sequentially could
        // block forever if the child fills one pipe while waiting for the other to drain.
        var stdoutTask = Task.Run(() => Drain(process.StandardOutput.BaseStream, stdout, replacements, encoding, sharedLock));
        var stderrTask = Task.Run(() => Drain(process.StandardError.BaseStream,  stderr, replacements, encoding, sharedLock));

        // WaitForExit() (no-argument overload) waits for the process to exit but does NOT
        // drain the redirected pipe streams when BaseStream is used directly (only the
        // Process.StandardOutput.ReadToEnd() / async-read path gets that behaviour).
        // Task.WaitAll below is therefore essential — it is the true EOF gate.
        process.WaitForExit();
        Task.WaitAll(stdoutTask, stderrTask);
        return process.ExitCode;
    }

    // Internal (not private) so RedactingPty.Tests can exercise the redaction
    // plumbing with in-memory streams, without spawning a subprocess.
    internal static void Drain(
        Stream source, Stream dest,
        IReadOnlyList<StreamReplacement> replacements,
        Encoding encoding, Lock? writeLock)
    {
        var readBuf  = new byte[4096];
        var decoder  = encoding.GetDecoder();
        var charBuf  = new char[encoding.GetMaxCharCount(readBuf.Length)];
        var redactor = new StreamRedactor(replacements);
        int n;
        while ((n = source.Read(readBuf, 0, readBuf.Length)) > 0)
        {
            var charCount = decoder.GetChars(readBuf, 0, n, charBuf, 0);
            var outBytes  = encoding.GetBytes(redactor.ProcessChunk(new string(charBuf, 0, charCount)));
            if (writeLock is not null)
                lock (writeLock) dest.Write(outBytes);
            else
                dest.Write(outBytes);
        }
        var tail = redactor.Flush();
        if (tail.Length > 0)
        {
            var tailBytes = encoding.GetBytes(tail);
            if (writeLock is not null)
                lock (writeLock) dest.Write(tailBytes);
            else
                dest.Write(tailBytes);
        }
    }
}

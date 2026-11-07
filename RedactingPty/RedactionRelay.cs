using System.Runtime.ExceptionServices;

namespace RedactingPty;

/// <summary>
/// A runner's redactor, failing closed. If the redactor (or a matcher it calls) throws, the held tail
/// and the failing chunk are discarded, <see cref="FailureMarker"/> is returned once in their place,
/// every later call returns <c>""</c> (the runner keeps draining the child's output, so the child never
/// blocks on a full PTY or pipe, but nothing after the failure point is written), and
/// <see cref="ThrowIfFailed"/> rethrows the exception once the child has exited.
/// </summary>
internal sealed class RedactionRelay(StreamRedactor redactor)
{
    /// <summary>Written in place of everything from the failing chunk on. It quotes no output.</summary>
    public const string FailureMarker = "[output truncated: redaction failed]";

    private ExceptionDispatchInfo? _failure;

    /// <summary>The failure, if the redactor threw.</summary>
    public ExceptionDispatchInfo? Failure => _failure;

    /// <summary>Whether the redactor holds text back (never after a failure: nothing will be released).</summary>
    public bool HasHeld => _failure is null && redactor.HasHeld;

    public string ProcessChunk(string chunk) => Guard(static (r, c) => r.ProcessChunk(c), chunk);

    public string Flush() => Guard(static (r, _) => r.Flush(), "");

    public string Release() => Guard(static (r, _) => r.Release(), "");

    /// <summary>Rethrows the redactor's exception, if there was one, with its original stack.</summary>
    public void ThrowIfFailed() => _failure?.Throw();

    private string Guard(Func<StreamRedactor, string, string> step, string chunk)
    {
        if (_failure is not null)
            return "";
        try
        {
            return step(redactor, chunk);
        }
        // Any exception: a matcher is caller code and may throw anything. It is not swallowed: it is
        // kept and rethrown after the child exits; until then no further output is written.
        catch (Exception ex)
        {
            _failure = ExceptionDispatchInfo.Capture(ex);
            return FailureMarker + Environment.NewLine;
        }
    }
}

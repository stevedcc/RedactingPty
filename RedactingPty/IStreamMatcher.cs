namespace RedactingPty;

/// <summary>
/// Finds matches in a <see cref="StreamRedactor"/>'s window, for find-strings the caller cannot or
/// will not hand over as plaintext (for example values known only by a keyed fingerprint).
/// The redactor calls <see cref="FindMatches"/> with its held tail plus each new chunk, and with the
/// tail alone on <see cref="StreamRedactor.Flush"/> and <see cref="StreamRedactor.Release"/>.
/// <para>
/// Implementations must be stateless per call and thread-safe: one matcher may serve several
/// redactors at once (the pipe runner drains stdout and stderr on two threads with one matcher).
/// </para>
/// </summary>
public interface IStreamMatcher
{
    /// <summary>
    /// The longest match <see cref="FindMatches"/> can report, in UTF-16 code units; at least 1.
    /// The redactor reads it once, when it is constructed.
    /// </summary>
    int MaxMatchLength { get; }

    /// <summary>
    /// Adds every match lying entirely inside <paramref name="window"/> to <paramref name="matches"/>,
    /// in window coordinates. Overlapping matches are allowed; the redactor merges them.
    /// </summary>
    void FindMatches(ReadOnlySpan<char> window, ICollection<StreamMatch> matches);

    /// <summary>
    /// The smallest index from which a match could still start in <paramref name="window"/> and end
    /// past it: the redactor may emit everything before it. <c>window.Length</c> when none can.
    /// Must lie in [0, <c>window.Length</c>]. The default holds the last
    /// <see cref="MaxMatchLength"/> − 1 characters.
    /// </summary>
    int PendingFrom(ReadOnlySpan<char> window) => Math.Max(0, window.Length - (MaxMatchLength - 1));
}

namespace RedactingPty;

/// <summary>Settings for <see cref="IPtyRunner.Run(string[], IReadOnlyList{StreamReplacement}, RedactionOptions)"/>.</summary>
public sealed record RedactionOptions
{
    /// <summary>Matchers applied alongside the plaintext replacements (see <see cref="IStreamMatcher"/>).</summary>
    public IReadOnlyList<IStreamMatcher> Matchers { get; init; } = [];

    /// <summary>
    /// Release held output when the child waits for terminal input, so a prompt is not hidden behind
    /// text the redactor holds back. Linux and macOS PTYs only; ignored by the Windows and pipe
    /// runners. Off by default. See the README, "Releasing held output at a prompt".
    /// </summary>
    public bool ReleaseWhenAwaitingInput { get; init; }

    /// <summary>
    /// Variables set for the child only, on top of the inherited environment (a name given here
    /// replaces the inherited value). Null or empty: the child inherits the environment unchanged.
    /// </summary>
    /// <remarks>
    /// The caller's process environment (<see cref="System.Environment"/> and the native one) is never
    /// changed, and the values never reach the child's command line. The parent's native copies are
    /// zeroed and freed after the spawn, best effort (the caller's managed strings can't be zeroed).
    /// A name is rejected with an <see cref="ArgumentException"/>, before anything is started, when it
    /// is null or empty or contains <c>=</c> or NUL, and a value when it is null or contains NUL (an
    /// empty value is allowed). Names and values must be valid text: on Linux and macOS they are passed
    /// as UTF-8, and an unpaired surrogate is replaced with U+FFFD (so the child gets a different string,
    /// which a <see cref="StreamReplacement"/> of the original no longer matches). On Linux and macOS
    /// the inherited variables are copied from the C library's <c>environ</c> in the caller's process
    /// before the spawn; that copy is not safe against another thread calling native <c>setenv</c>,
    /// <c>putenv</c> or <c>unsetenv</c> at the same time (<see cref="System.Environment"/> never does
    /// on Unix). Names compare ordinally on Linux and macOS (<c>Path</c> and <c>PATH</c>
    /// are two variables); on Windows two names equal ignoring case are rejected. On Linux and macOS a
    /// <c>PATH</c> given here is the one searched for the program; on Windows the program is found
    /// through the caller's <c>PATH</c>. Record equality compares the dictionary by reference, and
    /// <see cref="ToString"/> shows the dictionary's own <c>ToString()</c> (a BCL dictionary's is its
    /// type name, never its contents).
    /// </remarks>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    /// <summary>Called after each input-wait check with its result (the test host's seam).</summary>
    internal Action<bool>? InputWaitChecked { get; init; }
}

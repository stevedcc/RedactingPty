using System.Text;

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

    /// <summary>
    /// Windows only: the command line's arguments as the caller built them, passed verbatim after the
    /// program instead of argv[1..] quoted by the runner. Null: argv as before.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The command line the child gets is the program token, then, when this is not empty, one space and
    /// the text unchanged; empty: the program token alone. The runners never add, remove, quote or escape
    /// anything in the text. The program token is the runner's, as without this option: <c>WindowsPty</c>
    /// writes <c>argv[0]</c> quoted (with <c>"</c> escaped) only when it is empty or holds whitespace or
    /// <c>"</c>; the pipe runner (<c>FallbackPty</c>) gives <c>argv[0]</c> to
    /// <see cref="System.Diagnostics.ProcessStartInfo.FileName"/> with this text as
    /// <see cref="System.Diagnostics.ProcessStartInfo.Arguments"/>, and .NET trims the name, then wraps it
    /// in <c>"</c> unless it already starts and ends with <c>"</c> (then it goes as it is, unescaped). So
    /// for an ordinary path the two runners differ only in whether the program token is quoted; a program
    /// that parses its own name by other rules is the caller's concern. The program is found as without
    /// this option (<c>CreateProcess</c> with no application name).
    /// </para>
    /// <para>
    /// <b>The text is the child's command line</b>: whoever can read the child process (on Windows anyone
    /// who can open it, and anything that records process command lines, such as process-creation
    /// auditing) reads it. A secret belongs in <see cref="Environment"/>, not here; a caller that puts one
    /// here takes that exposure, as it would through argv.
    /// </para>
    /// <para>
    /// <c>CreateProcess</c> takes at most 32,767 characters of command line; a longer one fails at the
    /// start with the runner's <c>CreateProcess failed</c> error (or .NET's). It is not validated
    /// beforehand.
    /// </para>
    /// <para>
    /// <see cref="ArgumentException"/>, before anything is started: this is set on any OS but Windows (every
    /// built-in runner checks it), or set while <c>argv</c> has more than one element (choose one form),
    /// or holds NUL (<c>CreateProcess</c> would cut the line there). CR, LF and every other character are
    /// allowed. <see cref="ToString"/> shows only the text's length, never the text; record equality
    /// compares it ordinally.
    /// </para>
    /// </remarks>
    public string? WindowsArguments { get; init; }

    /// <summary>Called after each input-wait check with its result (the test host's seam).</summary>
    internal Action<bool>? InputWaitChecked { get; init; }

    // Replaces the generated PrintMembers so ToString never shows the WindowsArguments text (it may hold
    // secrets). It prints every public property as the generated one does: a new public property must be
    // added here.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Matchers = ").Append(Matchers);
        builder.Append(", ReleaseWhenAwaitingInput = ").Append(ReleaseWhenAwaitingInput);
        builder.Append(", Environment = ").Append(Environment);
        builder.Append(", WindowsArguments = ");
        builder.Append(WindowsArguments is null ? "null" : $"<{WindowsArguments.Length} characters>");
        return true;
    }
}

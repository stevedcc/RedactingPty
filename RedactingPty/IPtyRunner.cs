namespace RedactingPty;

/// <summary>
/// Strategy interface for platform-specific PTY execution.
/// Implementations spawn a command inside a pseudo-terminal and stream
/// its output — with all <see cref="StreamReplacement"/>s applied — back
/// to the caller's stdout.
/// </summary>
public interface IPtyRunner
{
    /// <summary>
    /// Directly executes <paramref name="argv"/>[0] with the remaining elements as its
    /// argument list (no shell wrapper), writing filtered output to stdout.
    /// Returns the child's exit code.
    /// </summary>
    int Run(string[] argv, IReadOnlyList<StreamReplacement> replacements);

    /// <summary>
    /// As <see cref="Run(string[], IReadOnlyList{StreamReplacement})"/>, with the
    /// <paramref name="options"/>' matchers applied alongside the plaintext replacements.
    /// The built-in runners implement it. The default delegates when the options ask for nothing
    /// and otherwise throws: a runner that does not know the options must not silently redact less.
    /// </summary>
    /// <exception cref="NotSupportedException">The runner does not support <see cref="RedactionOptions"/>.</exception>
    int Run(string[] argv, IReadOnlyList<StreamReplacement> replacements, RedactionOptions options)
        => options.Matchers.Count == 0 && !options.ReleaseWhenAwaitingInput
            ? Run(argv, replacements)
            : throw new NotSupportedException("This IPtyRunner does not support RedactionOptions.");
}

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
    /// <paramref name="options"/>' matchers applied alongside the plaintext replacements and
    /// <see cref="RedactionOptions.Environment"/> set for the child.
    /// The built-in runners implement it. The default delegates to the two-argument
    /// <see cref="Run(string[], IReadOnlyList{StreamReplacement})"/> only when the options ask for
    /// nothing (no <see cref="RedactionOptions.Matchers"/>, <see cref="RedactionOptions.ReleaseWhenAwaitingInput"/>
    /// off, <see cref="RedactionOptions.Environment"/> null or empty) and otherwise throws: a runner
    /// that does not know the options must not silently do less. A runner that overrides this overload
    /// enforces that rule itself.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="NotSupportedException">The runner does not support <see cref="RedactionOptions"/>.</exception>
    int Run(string[] argv, IReadOnlyList<StreamReplacement> replacements, RedactionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Matchers.Count == 0 && !options.ReleaseWhenAwaitingInput && options.Environment is null or { Count: 0 }
            ? Run(argv, replacements)
            : throw new NotSupportedException("This IPtyRunner does not support RedactionOptions.");
    }
}

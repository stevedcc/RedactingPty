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

    /// <summary>Called after each input-wait check with its result (the test host's seam).</summary>
    internal Action<bool>? InputWaitChecked { get; init; }
}

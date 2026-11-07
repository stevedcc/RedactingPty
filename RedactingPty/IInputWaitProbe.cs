namespace RedactingPty;

/// <summary>
/// Tells whether the child waits for terminal input (a prompt), so a runner may release held output.
/// Built by the runner in the parent, after the fork, from the PTY master. Must never throw: any
/// doubt answers false, and the redactor keeps holding.
/// </summary>
internal interface IInputWaitProbe
{
    bool ChildAwaitsInput();
}

/// <summary>The few <c>/proc</c> operations <see cref="LinuxInputWaitProbe"/> needs; null on any error.</summary>
internal interface IProcFs
{
    /// <summary>A file's text, or null if it can't be read.</summary>
    string? ReadText(string path);

    /// <summary>The names of a folder's entries, or null if it can't be listed.</summary>
    IReadOnlyList<string>? List(string path);

    /// <summary>
    /// The device numbers (<c>st_rdev</c>) of what <paramref name="path"/> names, following links (so
    /// <c>/proc/[pid]/fd/[n]</c> gives the open file's), or null on error. (0, 0) for a non-device.
    /// </summary>
    (uint Major, uint Minor)? DeviceOf(string path);
}

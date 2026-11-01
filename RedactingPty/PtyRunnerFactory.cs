namespace RedactingPty;

/// <summary>
/// Factory that resolves the platform-appropriate <see cref="IPtyRunner"/> at runtime.
/// This is the single place in the library that knows which platforms exist.
/// Adding a new platform: create a new <see cref="IPtyRunner"/> class and add one arm
/// to the switch expression in <see cref="Create"/>.
/// </summary>
public static class PtyRunnerFactory
{
    /// <summary>
    /// Picks the right <see cref="IPtyRunner"/> for the current OS and console state:
    /// a real PTY (<see cref="LinuxPty"/>/<see cref="MacOSPty"/>/<see cref="WindowsPty"/>) when
    /// stdin/stdout/stderr are all attached to a terminal, otherwise <see cref="FallbackPty"/>.
    /// </summary>
    public static IPtyRunner Create()
    {
        // Each OS gets its real PTY only when usePty; every other combination (redirected
        // stdio, Windows older than 1809, any other OS) gets FallbackPty.
        // Fall back to process-based I/O when stdout, stdin, or stderr is redirected:
        //   - Redirected stdout: PTY master bytes would corrupt a downstream pipe consumer.
        //   - Redirected stdin:  PTY can't half-close its input side, so the child would hang
        //                        waiting for EOF after stdin is exhausted.
        //   - Redirected stderr: PTY merges stderr into stdout, silently ignoring the redirect.
        // ConPTY requires Windows 10 1809 (build 17763) or later.
        bool usePty = !Console.IsOutputRedirected && !Console.IsInputRedirected && !Console.IsErrorRedirected;
        return usePty switch
        {
            true when OperatingSystem.IsLinux() => new LinuxPty(),
            true when OperatingSystem.IsMacOS() => new MacOSPty(),
            true when OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763) => new WindowsPty(),
            _ => new FallbackPty(),
        };
    }
}

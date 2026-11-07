namespace RedactingPty;

/// <summary>
/// Decides when a runner's relay asks the child's terminal whether it waits for input, so held text
/// (see <see cref="StreamRedactor.HasHeld"/>) can be released at a prompt. While text is held and a
/// probe exists, the first check comes after <see cref="FirstCheck"/> of quiet output, then one every
/// <see cref="Recheck"/> while output stays quiet. Only a positive check releases; time alone never
/// does. No probe (the option is off, or the platform has no signal), or nothing held: no checks.
/// </summary>
internal sealed class HoldReleaser(TimeProvider time, IInputWaitProbe? probe)
{
    /// <summary>The relay's poll timeout when no check is due: the runners' 200 ms cadence.</summary>
    public const int IdlePollMilliseconds = 200;

    public static readonly TimeSpan FirstCheck = TimeSpan.FromMilliseconds(50);
    public static readonly TimeSpan Recheck = TimeSpan.FromMilliseconds(100);

    private long _quietSince = time.GetTimestamp();
    private long? _lastCheck;

    /// <summary>Raised after each check with its result (the test seam).</summary>
    public event Action<bool>? Checked;

    /// <summary>
    /// A releaser for a run: with <paramref name="enabled"/> off the probe factory is never called
    /// (nothing about the child's terminal is read).
    /// </summary>
    public static HoldReleaser Create(bool enabled, TimeProvider time, Func<IInputWaitProbe?> probe) =>
        new(time, enabled ? probe() : null);

    /// <summary>The child wrote output: quiet starts again.</summary>
    public void OnOutput()
    {
        _quietSince = time.GetTimestamp();
        _lastCheck = null;
    }

    /// <summary>How long the relay may wait for output before it must call <see cref="ShouldRelease"/>.</summary>
    public int PollTimeoutMilliseconds(bool hasHeld)
    {
        if (!hasHeld || probe is null)
            return IdlePollMilliseconds;
        var left = time.GetElapsedTime(time.GetTimestamp(), Due());
        // Rounded up, so a poll that times out never finds the check still a fraction of a ms early.
        return (int)Math.Clamp(Math.Ceiling(left.TotalMilliseconds), 0, IdlePollMilliseconds);
    }

    /// <summary>Runs the check when it is due; true only when the probe says the child waits for input.</summary>
    public bool ShouldRelease(bool hasHeld)
    {
        if (!hasHeld || probe is null)
            return false;
        var now = time.GetTimestamp();
        if (now < Due())
            return false;
        var awaits = probe.ChildAwaitsInput();
        _lastCheck = now;
        Checked?.Invoke(awaits);
        return awaits;
    }

    private long Due() => _lastCheck is { } last
        ? last + (long)(Recheck.TotalSeconds * time.TimestampFrequency)
        : _quietSince + (long)(FirstCheck.TotalSeconds * time.TimestampFrequency);
}

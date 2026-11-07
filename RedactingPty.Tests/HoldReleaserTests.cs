using Xunit;

namespace RedactingPty.Tests;

/// <summary>
/// <see cref="HoldReleaser"/> over a fake clock and probe: with held text and a probe it checks at
/// 50 ms of quiet, then every 100 ms while output stays quiet, and releases only on a positive check.
/// Time alone never releases.
/// </summary>
public class HoldReleaserTests
{
    private sealed class FakeTime : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(int milliseconds) => _ticks += TimeSpan.FromMilliseconds(milliseconds).Ticks;
    }

    private sealed class FakeProbe : IInputWaitProbe
    {
        public bool Answer { get; set; }
        public int Calls { get; private set; }

        public bool ChildAwaitsInput()
        {
            Calls++;
            return Answer;
        }
    }

    private static (HoldReleaser Releaser, FakeTime Time, FakeProbe Probe, List<bool> Checks) Make(bool answer = false)
    {
        var time = new FakeTime();
        var probe = new FakeProbe { Answer = answer };
        var releaser = new HoldReleaser(time, probe);
        List<bool> checks = [];
        releaser.Checked += checks.Add;
        return (releaser, time, probe, checks);
    }

    [Fact]
    public void NoHeldText_NoCheck()
    {
        var (releaser, time, probe, _) = Make(answer: true);
        releaser.OnOutput();

        for (var i = 0; i < 20; i++)
        {
            time.Advance(100);
            Assert.False(releaser.ShouldRelease(hasHeld: false));
            Assert.Equal(HoldReleaser.IdlePollMilliseconds, releaser.PollTimeoutMilliseconds(hasHeld: false));
        }
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public void FirstCheckAt50msQuiet_ThenEvery100ms()
    {
        var (releaser, time, probe, checks) = Make();
        releaser.OnOutput();

        Assert.Equal(50, releaser.PollTimeoutMilliseconds(hasHeld: true));
        time.Advance(49);
        Assert.False(releaser.ShouldRelease(hasHeld: true));
        Assert.Equal(0, probe.Calls);
        Assert.Equal(1, releaser.PollTimeoutMilliseconds(hasHeld: true));

        time.Advance(1); // 50 ms quiet
        Assert.False(releaser.ShouldRelease(hasHeld: true));
        Assert.Equal(1, probe.Calls);
        Assert.Equal(100, releaser.PollTimeoutMilliseconds(hasHeld: true));

        time.Advance(99);
        Assert.False(releaser.ShouldRelease(hasHeld: true));
        Assert.Equal(1, probe.Calls);

        time.Advance(1); // 150 ms quiet
        releaser.ShouldRelease(hasHeld: true);
        time.Advance(100); // 250 ms quiet
        releaser.ShouldRelease(hasHeld: true);
        Assert.Equal(3, probe.Calls);
        Assert.Equal([false, false, false], checks);
    }

    [Fact]
    public void OutputResetsQuiet()
    {
        var (releaser, time, probe, _) = Make();
        releaser.OnOutput();
        time.Advance(50);
        releaser.ShouldRelease(hasHeld: true);
        Assert.Equal(1, probe.Calls);

        time.Advance(80);
        releaser.OnOutput(); // new output: quiet starts again, the next check is at 50 ms
        time.Advance(40);
        Assert.False(releaser.ShouldRelease(hasHeld: true));
        Assert.Equal(1, probe.Calls);
        time.Advance(10);
        releaser.ShouldRelease(hasHeld: true);
        Assert.Equal(2, probe.Calls);
    }

    [Fact]
    public void OptionOff_NoCheck()
    {
        var time = new FakeTime();
        var factoryCalls = 0;
        var releaser = HoldReleaser.Create(enabled: false, time, () => { factoryCalls++; return new FakeProbe { Answer = true }; });
        releaser.OnOutput();

        for (var i = 0; i < 20; i++)
        {
            time.Advance(100);
            Assert.False(releaser.ShouldRelease(hasHeld: true));
            Assert.Equal(HoldReleaser.IdlePollMilliseconds, releaser.PollTimeoutMilliseconds(hasHeld: true));
        }
        Assert.Equal(0, factoryCalls);
    }

    [Fact]
    public void NoProbe_NoCheck()
    {
        var time = new FakeTime();
        var releaser = HoldReleaser.Create(enabled: true, time, () => null);
        releaser.OnOutput();
        time.Advance(1000);

        Assert.False(releaser.ShouldRelease(hasHeld: true));
        Assert.Equal(HoldReleaser.IdlePollMilliseconds, releaser.PollTimeoutMilliseconds(hasHeld: true));
    }

    [Fact]
    public void ProbeFalse_NeverReleases()
    {
        var (releaser, time, probe, checks) = Make(answer: false);
        releaser.OnOutput();

        for (var i = 0; i < 600; i++) // a minute of quiet
        {
            time.Advance(100);
            Assert.False(releaser.ShouldRelease(hasHeld: true));
        }
        Assert.Equal(600, probe.Calls);
        Assert.DoesNotContain(true, checks);
    }

    [Fact]
    public void ProbeTrue_Releases()
    {
        var (releaser, time, _, checks) = Make(answer: true);
        releaser.OnOutput();
        time.Advance(50);

        Assert.True(releaser.ShouldRelease(hasHeld: true));
        Assert.Equal([true], checks);
    }
}

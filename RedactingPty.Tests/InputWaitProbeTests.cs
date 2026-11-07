using System.Runtime.InteropServices;
using Xunit;

namespace RedactingPty.Tests;

/// <summary>
/// <see cref="LinuxInputWaitProbe"/> over a fake <c>/proc</c> (<see cref="IProcFs"/>) and
/// <see cref="MacInputWaitProbe"/>'s flag rule. Every doubt (an unreadable file, a malformed line, a
/// failed <c>tcgetpgrp</c>) answers false: the redactor keeps holding.
/// </summary>
public class InputWaitProbeTests
{
    private const int ForegroundGroup = 4000;
    private static readonly (uint Major, uint Minor) Slave = (136, 7);    // /dev/pts/7
    private static readonly (uint Major, uint Minor) OtherPts = (136, 9);
    private static readonly (uint Major, uint Minor) DevTty = (5, 0);
    private const int ReadX64 = 0;

    /// <summary>tty_nr as the kernel encodes it in /proc/[pid]/stat.</summary>
    private static int TtyNr((uint Major, uint Minor) dev) =>
        (int)((dev.Minor & 0xff) | (dev.Major << 8) | ((dev.Minor & ~0xffu) << 12));

    private sealed class FakeProcFs : IProcFs
    {
        public Dictionary<string, string> Files { get; } = [];
        public Dictionary<string, List<string>> Folders { get; } = [];
        public Dictionary<string, (uint, uint)> Devices { get; } = [];

        public string? ReadText(string path) => Files.GetValueOrDefault(path);
        public IReadOnlyList<string>? List(string path) => Folders.TryGetValue(path, out var names) ? names : null;
        public (uint Major, uint Minor)? DeviceOf(string path) => Devices.TryGetValue(path, out var d) ? d : null;

        /// <summary>Adds a process with its threads' syscall lines and fds.</summary>
        public FakeProcFs Process(int pid, int pgrp, (uint, uint) controllingTty, string command = "prog",
            string[]? threads = null, Dictionary<int, (uint, uint)>? fds = null)
        {
            if (!Folders.TryGetValue("/proc", out var pids))
                Folders["/proc"] = pids = ["self", "cpuinfo"];
            pids.Add(pid.ToString());
            Files[$"/proc/{pid}/stat"] = $"{pid} ({command}) S 1 {pgrp} {pgrp} {TtyNr(controllingTty)} {pgrp} 4194304 0 0 0";
            threads ??= ["0 0x0 0x7ffc 0x1 0x0 0x0 0x0 0x7ffd 0x7f00"];
            Folders[$"/proc/{pid}/task"] = [];
            for (var t = 0; t < threads.Length; t++)
            {
                var tid = pid + t;
                Folders[$"/proc/{pid}/task"].Add(tid.ToString());
                Files[$"/proc/{pid}/task/{tid}/syscall"] = threads[t] + "\n";
            }
            foreach (var (fd, dev) in fds ?? new() { [0] = Slave })
                Devices[$"/proc/{pid}/fd/{fd}"] = dev;
            return this;
        }
    }

    private static LinuxInputWaitProbe Probe(FakeProcFs fs, int foregroundGroup = ForegroundGroup) =>
        new(() => foregroundGroup, Slave, fs, ReadX64);

    private const string ReadFd0 = "0 0x0 0x7ffc1000 0x1 0x0 0x0 0x0 0x7ffc0ff8 0x7f0000001234";
    private const string ReadFd5 = "0 0x5 0x7ffc1000 0x1 0x0 0x0 0x0 0x7ffc0ff8 0x7f0000001234";
    private const string Futex = "202 0x7f00 0x80 0x0 0x0 0x0 0x0 0x7ffc0ff8 0x7f0000001234";
    private const string Nanosleep = "230 0x0 0x0 0x7ffc 0x0 0x0 0x0 0x7ffc0ff8 0x7f0000001234";

    [Fact]
    public void AllThreadsBlockedInReadOnSlave_True()
    {
        var fs = new FakeProcFs().Process(4000, ForegroundGroup, Slave, threads: [ReadFd0, Futex, Futex]);
        Assert.True(Probe(fs).ChildAwaitsInput());
    }

    [Fact]
    public void ReadOnSlaveThroughAnyFd_True()
    {
        // The slave by another path or a dup'ed descriptor: compared by device numbers.
        var fs = new FakeProcFs().Process(4000, ForegroundGroup, Slave, threads: [ReadFd5], fds: new() { [5] = Slave });
        Assert.True(Probe(fs).ChildAwaitsInput());
    }

    [Fact]
    public void ReadOnDevTty_ControllingTerminalIsSlave_True()
    {
        // getpass: read(5) on /dev/tty, whose device is (5, 0), not the slave's.
        var fs = new FakeProcFs().Process(4000, ForegroundGroup, Slave, threads: [ReadFd5], fds: new() { [0] = Slave, [5] = DevTty });
        Assert.True(Probe(fs).ChildAwaitsInput());
    }

    [Fact]
    public void ReadOnDevTty_OtherControllingTerminal_False()
    {
        var fs = new FakeProcFs().Process(4000, ForegroundGroup, OtherPts, threads: [ReadFd5], fds: new() { [5] = DevTty });
        Assert.False(Probe(fs).ChildAwaitsInput());
    }

    [Fact]
    public void ReadOnAnotherPts_False()
    {
        var fs = new FakeProcFs().Process(4000, ForegroundGroup, Slave, threads: [ReadFd0], fds: new() { [0] = OtherPts });
        Assert.False(Probe(fs).ChildAwaitsInput());
    }

    [Fact]
    public void ReadOnAPipe_False()
    {
        // A pipe has no device numbers of a terminal: (0, 0).
        var fs = new FakeProcFs().Process(4000, ForegroundGroup, Slave, threads: [ReadFd0], fds: new() { [0] = (0, 0) });
        Assert.False(Probe(fs).ChildAwaitsInput());
    }

    [Fact]
    public void OneThreadRunning_False()
    {
        var fs = new FakeProcFs().Process(4000, ForegroundGroup, Slave, threads: [ReadFd0, "running"]);
        Assert.False(Probe(fs).ChildAwaitsInput());
    }

    [Fact]
    public void OtherMemberRunning_False()
    {
        var fs = new FakeProcFs()
            .Process(4000, ForegroundGroup, Slave, threads: [ReadFd0])
            .Process(4100, ForegroundGroup, Slave, threads: ["running"]);
        Assert.False(Probe(fs).ChildAwaitsInput());
    }

    [Fact]
    public void SleepingInClockNanosleep_False()
    {
        var fs = new FakeProcFs().Process(4000, ForegroundGroup, Slave, threads: [Nanosleep]);
        Assert.False(Probe(fs).ChildAwaitsInput());
    }

    [Fact]
    public void UnreadableSyscallFile_False()
    {
        var fs = new FakeProcFs().Process(4000, ForegroundGroup, Slave, threads: [ReadFd0, Futex]);
        fs.Files.Remove("/proc/4000/task/4001/syscall");
        Assert.False(Probe(fs).ChildAwaitsInput());
    }

    [Fact]
    public void UnreadableFdLink_False()
    {
        var fs = new FakeProcFs().Process(4000, ForegroundGroup, Slave, threads: [ReadFd0]);
        fs.Devices.Remove("/proc/4000/fd/0");
        Assert.False(Probe(fs).ChildAwaitsInput());
    }

    [Fact]
    public void UnreadableStatOfAnyProcess_False()
    {
        // A process that exits mid-scan: listed, but its stat is gone. It could have been a member.
        var fs = new FakeProcFs()
            .Process(4000, ForegroundGroup, Slave, threads: [ReadFd0])
            .Process(5000, 5000, OtherPts);
        fs.Files.Remove("/proc/5000/stat");
        Assert.False(Probe(fs).ChildAwaitsInput());
    }

    [Fact]
    public void CommandNameWithParenAndSpaces_ParsedAfterLastParen()
    {
        // A command name may hold ")" and spaces; the fields are parsed after the last ")".
        var fs = new FakeProcFs().Process(4000, ForegroundGroup, Slave, command: "evil) S 1 9999 9999 0 (x", threads: [ReadFd0]);
        Assert.True(Probe(fs).ChildAwaitsInput());
    }

    [Theory]
    [InlineData("4000 (prog S 1 4000 4000 34823")]   // no ")"
    [InlineData("4000 (prog) S 1")]                  // too few fields
    [InlineData("4000 (prog) S 1 x 4000 34823 4000")] // non-numeric pgrp
    [InlineData("4000 (prog) S 1 4000 4000 y 4000")]  // non-numeric tty_nr
    public void MalformedStat_False(string stat)
    {
        var fs = new FakeProcFs().Process(4000, ForegroundGroup, Slave, threads: [ReadFd0]);
        fs.Files["/proc/4000/stat"] = stat;
        Assert.False(Probe(fs).ChildAwaitsInput());
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]                 // read without its arguments
    [InlineData("0 zz 0x0")]          // a non-hex fd
    [InlineData("nonsense 0x0 0x0")]
    public void MalformedSyscallLine_False(string line)
    {
        var fs = new FakeProcFs().Process(4000, ForegroundGroup, Slave, threads: [line]);
        Assert.False(Probe(fs).ChildAwaitsInput());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void TcgetpgrpFailure_False(int result)
    {
        // Kernel threads sit in process group 0: a failed tcgetpgrp must not match them, or anything.
        var fs = new FakeProcFs()
            .Process(4000, ForegroundGroup, Slave, threads: [ReadFd0])
            .Process(4100, result, Slave, threads: [ReadFd0]);
        Assert.False(Probe(fs, foregroundGroup: result).ChildAwaitsInput());
    }

    [Fact]
    public void NotInForegroundGroup_Ignored()
    {
        // A background process of another group that runs does not stop the release.
        var fs = new FakeProcFs()
            .Process(4000, ForegroundGroup, Slave, threads: [ReadFd0])
            .Process(4200, 4200, Slave, threads: ["running"]);
        Assert.True(Probe(fs).ChildAwaitsInput());
    }

    [Fact]
    public void NoMemberInTheGroup_False()
    {
        var fs = new FakeProcFs().Process(4200, 4200, Slave, threads: [ReadFd0]);
        Assert.False(Probe(fs).ChildAwaitsInput());
    }

    [Fact]
    public void UnknownArchitecture_NoProbe()
    {
        Assert.Equal(0, LinuxInputWaitProbe.ReadSyscallNumber(Architecture.X64));
        Assert.Equal(63, LinuxInputWaitProbe.ReadSyscallNumber(Architecture.Arm64));
        Assert.Null(LinuxInputWaitProbe.ReadSyscallNumber(Architecture.X86));
        Assert.Null(LinuxInputWaitProbe.ReadSyscallNumber(Architecture.Arm));
        Assert.Null(LinuxInputWaitProbe.TryCreate(masterFd: -1, Architecture.RiscV64));
    }

    // ── macOS: the terminal's local flags ────────────────────────────────────────────

    private const ulong Echo = 0x8, Icanon = 0x100; // <sys/termios.h> on macOS

    [Fact]
    public void Mac_EchoOffCanonical_Releases() => Assert.True(MacInputWaitProbe.Releases(Icanon));

    [Fact]
    public void Mac_EchoOn_Holds() => Assert.False(MacInputWaitProbe.Releases(Echo | Icanon));

    /// <summary>
    /// The ICANON condition alone: a raw-mode program (vim, less) clears both ECHO and ICANON and would
    /// otherwise release on every pause; password prompts keep canonical mode.
    /// </summary>
    [Fact]
    public void Mac_EchoOffRawMode_Holds() => Assert.False(MacInputWaitProbe.Releases(0));

    // Which terminal's flags the macOS probe reads: the slave's (opened for each check), else the
    // master's, else none (hold).

    [Fact]
    public void Mac_SlaveFlagsReadable_SlaveUsed_MasterNotAsked()
    {
        var masterAsked = false;
        var probe = new MacInputWaitProbe(slaveFlags: () => Icanon, masterFlags: () => { masterAsked = true; return Echo | Icanon; });

        Assert.True(probe.ChildAwaitsInput());
        Assert.False(masterAsked);
    }

    [Fact]
    public void Mac_SlaveUnreadable_MasterUsed()
    {
        Assert.True(new MacInputWaitProbe(slaveFlags: () => null, masterFlags: () => Icanon).ChildAwaitsInput());
        Assert.False(new MacInputWaitProbe(slaveFlags: () => null, masterFlags: () => Echo | Icanon).ChildAwaitsInput());
    }

    [Fact]
    public void Mac_NeitherReadable_Holds()
    {
        Assert.False(new MacInputWaitProbe(slaveFlags: () => null, masterFlags: () => null).ChildAwaitsInput());
    }

    [Fact]
    public void Mac_SlaveSaysRawMode_Holds_EvenIfMasterWouldRelease()
    {
        Assert.False(new MacInputWaitProbe(slaveFlags: () => 0, masterFlags: () => Icanon).ChildAwaitsInput());
    }
}

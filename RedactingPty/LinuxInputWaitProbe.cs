using System.Globalization;
using System.Runtime.InteropServices;

namespace RedactingPty;

/// <summary>
/// Linux: the child waits for input when no thread of any process in the PTY's foreground process
/// group (<c>tcgetpgrp</c> on the master) is running and at least one is blocked in <c>read</c> on our
/// terminal: a descriptor whose device is the slave's (by any path, or <c>dup</c>ed), or <c>/dev/tty</c>
/// (5, 0) in a process whose controlling terminal is the slave (<c>getpass</c> reads fd 5 = /dev/tty).
/// Thread states come from <c>/proc/[pid]/task/[tid]/syscall</c>. Any running thread, any file that
/// can't be read or parsed (a process exiting mid-scan, Yama <c>ptrace_scope</c> &gt;= 2), or a failed
/// <c>tcgetpgrp</c>: false. A process <c>/proc</c> doesn't list at all (another user's, with <c>hidepid=2</c>,
/// e.g. a setuid helper in the foreground group) can't be seen, so its threads are not checked: a
/// release may then come while it runs (what it prints later is the residual limit). Programs that wait in <c>poll</c>, <c>select</c> or <c>epoll</c>
/// (bash's readline waits in <c>pselect6</c>) are not detected: their prompt stays held.
/// </summary>
internal sealed partial class LinuxInputWaitProbe(Func<int> foregroundGroup, (uint Major, uint Minor) slave, IProcFs procFs, int readSyscall)
    : IInputWaitProbe
{
    private static readonly (uint Major, uint Minor) DevTty = (5, 0);

    /// <summary>The number of <c>read</c> in <c>/proc/[pid]/syscall</c>; null where the probe isn't supported.</summary>
    public static int? ReadSyscallNumber(Architecture architecture) => architecture switch
    {
        Architecture.X64 => 0,
        Architecture.Arm64 => 63,
        _ => null,
    };

    /// <summary>
    /// The probe for <paramref name="masterFd"/>, or null when the architecture has no known
    /// <c>read</c> number or the slave can't be named and stat'ed. Called in the parent only.
    /// </summary>
    public static LinuxInputWaitProbe? TryCreate(int masterFd, Architecture architecture)
    {
        if (!OperatingSystem.IsLinux() || ReadSyscallNumber(architecture) is not { } read)
            return null;
        var name = new byte[128];
        if (ptsname_r(masterFd, name, name.Length) != 0)
            return null;
        var slavePath = System.Text.Encoding.UTF8.GetString(name, 0, Math.Max(0, Array.IndexOf(name, (byte)0)));
        if (slavePath.Length == 0 || ProcFs.Statx(slavePath) is not { } slave)
            return null;
        return new LinuxInputWaitProbe(() => tcgetpgrp(masterFd), slave, ProcFs.Instance, read);
    }

    [LibraryImport("libc", EntryPoint = "ptsname_r")]
    private static partial int ptsname_r(int fd, [Out] byte[] buf, nint buflen);

    [LibraryImport("libc", EntryPoint = "tcgetpgrp")]
    private static partial int tcgetpgrp(int fd);

    public bool ChildAwaitsInput()
    {
        var group = foregroundGroup();
        if (group <= 0)
            return false;
        if (procFs.List("/proc") is not { } entries)
            return false;

        List<(int Pid, int TtyNr)> members = [];
        foreach (var entry in entries)
        {
            if (!int.TryParse(entry, NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
                continue;
            // Every process's stat must be readable: one that can't be read could be a member.
            if (procFs.ReadText($"/proc/{pid}/stat") is not { } stat || ParseStat(stat) is not { } fields)
                return false;
            if (fields.Pgrp == group)
                members.Add((pid, fields.TtyNr));
        }
        if (members.Count == 0)
            return false;

        var reading = false;
        foreach (var (pid, ttyNr) in members)
        {
            if (procFs.List($"/proc/{pid}/task") is not { Count: > 0 } tids)
                return false;
            foreach (var tid in tids)
            {
                if (procFs.ReadText($"/proc/{pid}/task/{tid}/syscall") is not { } line)
                    return false;
                switch (ParseSyscall(line))
                {
                    case null:
                        return false; // running, or not understood
                    case (var nr, var fd) when nr == readSyscall:
                        if (procFs.DeviceOf($"/proc/{pid}/fd/{fd}") is not { } device)
                            return false;
                        if (device == slave || (device == DevTty && DecodeTtyNr(ttyNr) == slave))
                            reading = true;
                        break;
                }
            }
        }
        return reading;
    }

    /// <summary>
    /// The pgrp and tty_nr fields of a <c>/proc/[pid]/stat</c> line, parsed after the last ")" (the
    /// command name may hold ")" and spaces); null on any anomaly.
    /// </summary>
    private static (int Pgrp, int TtyNr)? ParseStat(string stat)
    {
        var close = stat.LastIndexOf(')');
        if (close < 0)
            return null;
        // After ") ": state ppid pgrp session tty_nr ...
        var fields = stat.AsSpan(close + 1).Trim();
        Span<Range> parts = stackalloc Range[6];
        if (fields.Split(parts, ' ') < 5)
            return null;
        if (!int.TryParse(fields[parts[2]], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var pgrp) ||
            !int.TryParse(fields[parts[4]], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var ttyNr))
            return null;
        return (pgrp, ttyNr);
    }

    /// <summary>
    /// A blocked thread's syscall number and first argument; null for "running" or anything else not
    /// understood. A thread blocked outside a syscall ("-1 sp pc") is blocked and reads nothing.
    /// </summary>
    private static (long Nr, long Arg0)? ParseSyscall(string line)
    {
        var text = line.AsSpan().Trim();
        Span<Range> parts = stackalloc Range[3];
        var count = text.Split(parts, ' ');
        if (count < 2 || !long.TryParse(text[parts[0]], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var nr))
            return null;
        if (nr == -1)
            return (-1, -1);
        var arg = text[parts[1]];
        if (!arg.StartsWith("0x") ||
            !long.TryParse(arg[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var arg0))
            return null;
        return (nr, arg0);
    }

    /// <summary>tty_nr → (major, minor), as the kernel's new_encode_dev packs it.</summary>
    private static (uint Major, uint Minor) DecodeTtyNr(int ttyNr)
    {
        var value = (uint)ttyNr;
        return ((value >> 8) & 0xfff, (value & 0xff) | ((value >> 12) & 0xfff00));
    }
}

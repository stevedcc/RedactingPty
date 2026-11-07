using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace RedactingPty;

/// <summary>
/// macOS has no <c>/proc</c>: the child is taken to wait for input when its terminal's ECHO flag is off
/// and ICANON is on. Password and passphrase prompts (ssh, sudo, gpg) turn echo off and keep canonical
/// mode; raw-mode programs such as vim or less clear both and would otherwise release on every pause. A
/// plain y/N prompt keeps echo on, so behind held text it stays held until more output or exit.
/// <para>
/// The flags are the line discipline's, read from the <b>slave</b>: for each check the parent opens the
/// slave (<c>O_RDONLY | O_NOCTTY | O_CLOEXEC</c>), calls <c>tcgetattr</c> and closes it at once. It is not
/// kept open: while the parent holds a slave descriptor the master never reports the end of the output
/// once the child has exited, and the relay would wait for its drain timeout. If the slave can't be
/// opened or read, the master's flags are used; if neither can be read, the output is held.
/// </para>
/// </summary>
internal sealed partial class MacInputWaitProbe(Func<ulong?> slaveFlags, Func<ulong?> masterFlags) : IInputWaitProbe
{
    // <sys/termios.h> on macOS: tcflag_t is unsigned long, so c_lflag is the fourth 8-byte field.
    private const ulong Echo = 0x00000008;
    private const ulong Icanon = 0x00000100;
    private const int LocalFlagsOffset = 24;
    private const int TermiosSize = 72;
    // <sys/fcntl.h> on macOS.
    private const int ORdOnly = 0x0000, ONoCtty = 0x20000, OCloExec = 0x01000000;

    /// <summary>The release rule on the terminal's local flags.</summary>
    public static bool Releases(ulong localFlags) =>
        (localFlags & Echo) == 0
        // ICANON on: canonical-mode prompts only (see the class comment).
        && (localFlags & Icanon) != 0;

    /// <summary>The probe for <paramref name="masterFd"/>: the slave's flags by path (named once, here), else the master's.</summary>
    [SupportedOSPlatform("macos")]
    public static MacInputWaitProbe Create(int masterFd)
    {
        var name = new byte[128];
        var slavePath = ptsname_r(masterFd, name, name.Length) == 0
            ? Encoding.UTF8.GetString(name, 0, Math.Max(0, Array.IndexOf(name, (byte)0)))
            : "";
        return new MacInputWaitProbe(
            slaveFlags: () => slavePath.Length == 0 ? null : SlaveFlags(slavePath),
            masterFlags: () => LocalFlags(masterFd));
    }

    public bool ChildAwaitsInput() => (slaveFlags() ?? masterFlags()) is { } flags && Releases(flags);

    private static ulong? SlaveFlags(string slavePath)
    {
        var fd = open(slavePath, ORdOnly | ONoCtty | OCloExec);
        if (fd < 0)
            return null;
        try { return LocalFlags(fd); }
        finally { close(fd); }
    }

    private static ulong? LocalFlags(int fd)
    {
        var termios = new byte[TermiosSize];
        return tcgetattr(fd, termios) == 0 ? BinaryPrimitives.ReadUInt64LittleEndian(termios.AsSpan(LocalFlagsOffset)) : null;
    }

    [LibraryImport("libc", EntryPoint = "tcgetattr")]
    private static partial int tcgetattr(int fd, [Out] byte[] termios);

    [LibraryImport("libc", EntryPoint = "ptsname_r")]
    private static partial int ptsname_r(int fd, [Out] byte[] buf, nint buflen);

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "close")]
    private static partial int close(int fd);
}

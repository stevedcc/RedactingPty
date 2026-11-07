using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace RedactingPty;

/// <summary>
/// macOS has no <c>/proc</c>: the child is taken to wait for input when the terminal's ECHO flag is off
/// and ICANON is on (<c>tcgetattr</c> on the master). Password and passphrase prompts (ssh, sudo, gpg)
/// turn echo off and keep canonical mode; raw-mode programs such as vim or less clear both and would
/// otherwise release on every pause. A plain y/N prompt keeps echo on, so behind held text it stays
/// held until more output or exit.
/// </summary>
internal sealed partial class MacInputWaitProbe(int masterFd) : IInputWaitProbe
{
    // <sys/termios.h> on macOS: tcflag_t is unsigned long, so c_lflag is the fourth 8-byte field.
    private const ulong Echo = 0x00000008;
    private const ulong Icanon = 0x00000100;
    private const int LocalFlagsOffset = 24;
    private const int TermiosSize = 72;

    /// <summary>The release rule on the terminal's local flags.</summary>
    public static bool Releases(ulong localFlags) =>
        (localFlags & Echo) == 0
        // ICANON on: canonical-mode prompts only (see the class comment).
        && (localFlags & Icanon) != 0;

    [LibraryImport("libc", EntryPoint = "tcgetattr")]
    private static partial int tcgetattr(int fd, [Out] byte[] termios);

    public bool ChildAwaitsInput()
    {
        var termios = new byte[TermiosSize];
        return tcgetattr(masterFd, termios) == 0
            && Releases(BinaryPrimitives.ReadUInt64LittleEndian(termios.AsSpan(LocalFlagsOffset)));
    }
}

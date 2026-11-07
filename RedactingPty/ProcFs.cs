using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RedactingPty;

/// <summary>The real <see cref="IProcFs"/>, and the device lookup the Linux probe uses for the slave.</summary>
[SupportedOSPlatform("linux")]
internal sealed partial class ProcFs : IProcFs
{
    public static readonly ProcFs Instance = new();

    public string? ReadText(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public IReadOnlyList<string>? List(string path)
    {
        try { return [.. Directory.EnumerateFileSystemEntries(path).Select(Path.GetFileName).OfType<string>()]; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public (uint Major, uint Minor)? DeviceOf(string path) => Statx(path);

    // statx(2) rather than stat(2): struct statx has one layout on every Linux architecture, so no
    // per-architecture struct stat is needed. stx_rdev_major and stx_rdev_minor are at offsets 128
    // and 132 of the 256-byte struct (include/uapi/linux/stat.h).
    private const int AtFdCwd = -100;
    private const uint StatxBasicStats = 0x7ff;
    private const int StatxSize = 256;

    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int statx(int dirfd, string pathname, int flags, uint mask, [Out] byte[] statxbuf);

    internal static (uint Major, uint Minor)? Statx(string path)
    {
        var buf = new byte[StatxSize];
        if (statx(AtFdCwd, path, 0, StatxBasicStats, buf) != 0)
            return null;
        return (BitConverter.ToUInt32(buf, 128), BitConverter.ToUInt32(buf, 132));
    }
}

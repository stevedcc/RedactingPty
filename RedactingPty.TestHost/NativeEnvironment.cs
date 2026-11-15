using System.Runtime.InteropServices;

namespace RedactingPty.TestHost;

/// <summary>The C library's view of this process's environment (Unix), beside the runtime's managed one.</summary>
internal static partial class NativeEnvironment
{
    [LibraryImport("libc", EntryPoint = "getenv", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint getenv(string name);

    /// <summary>libc <c>getenv(name)</c>; empty when unset.</summary>
    public static string Get(string name) => Marshal.PtrToStringUTF8(getenv(name)) ?? "";
}

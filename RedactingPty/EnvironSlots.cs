namespace RedactingPty;

/// <summary>
/// Picks the addresses of the C environment pointer that <see cref="UnixPty"/>'s forked child stores
/// its environment block to (Linux). Pure: the lookups are passed in (0 = not exported), so the rule
/// is tested on every OS.
/// </summary>
internal static class EnvironSlots
{
    /// <summary>
    /// <c>Primary</c>: <c>__environ</c> in the global scope, the name glibc's and musl's execvp and getenv
    /// read; <c>environ</c> there when <c>__environ</c> isn't exported. <c>Alias</c>: global-scope
    /// <c>environ</c> when it is a different address (a copy relocation of <c>environ</c> in the
    /// executable gives it its own slot), else 0. Without <c>__environ</c> the global-scope
    /// <c>environ</c> may itself be such a copy, so <c>Alias</c> is then libc's own <c>environ</c>
    /// (looked up in libc) when that differs. Neither name found: throws.
    /// </summary>
    public static (nint Primary, nint Alias) ResolveLinux(Func<string, nint> fromGlobalScope, Func<string, nint> fromLibc)
    {
        var primary = fromGlobalScope("__environ");
        var environ = fromGlobalScope("environ");
        if (primary == 0)
        {
            if (environ == 0)
                throw new PlatformNotSupportedException("redactingpty: the C library exports neither __environ nor environ.");
            var libcEnviron = fromLibc("environ");
            return (environ, libcEnviron != 0 && libcEnviron != environ ? libcEnviron : 0);
        }
        return (primary, environ != 0 && environ != primary ? environ : 0);
    }
}

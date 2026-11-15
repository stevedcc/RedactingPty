namespace RedactingPty;

/// <summary>
/// <see cref="RedactionOptions.Environment"/>'s rules, in one place: <see cref="Validate"/> runs in
/// <see cref="MatcherCarrier.For"/>, which every built-in runner's options overload goes through before
/// anything is spawned.
/// </summary>
internal static class ChildEnvironment
{
    /// <summary>
    /// Throws <see cref="ArgumentException"/> when a name is null or empty or contains <c>=</c> or NUL,
    /// or a value is null or contains NUL; on Windows also when two names are equal ignoring case.
    /// </summary>
    public static void Validate(IReadOnlyDictionary<string, string>? environment)
    {
        if (environment is null)
            return;
        var seen = OperatingSystem.IsWindows() ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : null;
        foreach (var (name, value) in environment)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("An environment variable name is null or empty.", nameof(environment));
            if (name.AsSpan().ContainsAny('=', '\0'))
                throw new ArgumentException($"The environment variable name '{name.Replace("\0", "\\0")}' contains '=' or NUL.", nameof(environment));
            // The value is never part of a message: it may be a secret.
            if (value is null)
                throw new ArgumentException($"The value of environment variable '{name}' is null.", nameof(environment));
            if (value.Contains('\0'))
                throw new ArgumentException($"The value of environment variable '{name}' contains NUL.", nameof(environment));
            if (seen is not null && !seen.Add(name))
                throw new ArgumentException($"Environment variable names that differ only in case: '{name}' (Windows names ignore case).", nameof(environment));
        }
    }

    /// <summary>
    /// The <c>lpEnvironment</c> block for <c>CreateProcessW</c> with <c>CREATE_UNICODE_ENVIRONMENT</c>:
    /// <paramref name="inherited"/> with <paramref name="given"/> applied (names compared
    /// <see cref="StringComparer.OrdinalIgnoreCase"/>, a given name replacing an inherited one, in the
    /// given name's case), sorted by name with <see cref="StringComparer.OrdinalIgnoreCase"/> (the
    /// documented order: case-insensitive, Unicode order, without regard to locale), each entry
    /// <c>name=value\0</c>, then one more <c>\0</c>.
    /// </summary>
    public static char[] BuildWindowsBlock(IReadOnlyDictionary<string, string> inherited, IReadOnlyDictionary<string, string> given)
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in inherited)
            merged[name] = value;
        foreach (var (name, value) in given)
        {
            merged.Remove(name); // the inherited name's case goes with it
            merged[name] = value;
        }
        var names = merged.Keys.ToArray();
        Array.Sort(names, StringComparer.OrdinalIgnoreCase);

        var length = 1;
        foreach (var name in names)
            length += name.Length + 1 + merged[name].Length + 1;
        if (names.Length == 0)
            length++; // an empty block is still two NULs
        var block = new char[length];
        var at = 0;
        foreach (var name in names)
        {
            name.CopyTo(block.AsSpan(at));
            at += name.Length;
            block[at++] = '=';
            merged[name].CopyTo(block.AsSpan(at));
            at += merged[name].Length;
            block[at++] = '\0';
        }
        // The array is zero-initialised: the remaining one (or two) chars are the terminator.
        return block;
    }
}

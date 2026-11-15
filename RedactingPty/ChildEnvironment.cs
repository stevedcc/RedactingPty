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
}

namespace RedactingPty;

/// <summary>
/// One match an <see cref="IStreamMatcher"/> reports: <paramref name="Length"/> characters from
/// <paramref name="Start"/> in the window it was given, to be replaced by <paramref name="Replacement"/>.
/// </summary>
public readonly record struct StreamMatch(int Start, int Length, string Replacement);

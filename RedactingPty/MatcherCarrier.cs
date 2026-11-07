using System.Collections;

namespace RedactingPty;

/// <summary>
/// Carries <see cref="RedactionOptions"/> to a runner's 1.0 <c>Run(argv, replacements)</c> inside the
/// replacement list, so the options overloads reuse that method unchanged (UnixPty's <c>Run</c> holds
/// the fork-safe child path, whose signature and code are frozen). It enumerates as the plain
/// replacements; <see cref="StreamRedactor.ForRunner"/> unwraps it. No instance state is added to the
/// runners, so concurrent <c>Run</c> calls stay independent.
/// </summary>
internal sealed class MatcherCarrier : IReadOnlyList<StreamReplacement>
{
    private readonly IReadOnlyList<StreamReplacement> _replacements;

    private MatcherCarrier(IReadOnlyList<StreamReplacement> replacements, RedactionOptions options)
    {
        _replacements = replacements;
        Options = options;
    }

    public RedactionOptions Options { get; }

    public IReadOnlyList<StreamReplacement> Replacements => _replacements;

    /// <summary>Wraps <paramref name="replacements"/> with <paramref name="options"/>.</summary>
    public static MatcherCarrier For(IReadOnlyList<StreamReplacement> replacements, RedactionOptions options)
    {
        ArgumentNullException.ThrowIfNull(replacements);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Matchers);
        return new MatcherCarrier(replacements, options);
    }

    public StreamReplacement this[int index] => _replacements[index];

    public int Count => _replacements.Count;

    public IEnumerator<StreamReplacement> GetEnumerator() => _replacements.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

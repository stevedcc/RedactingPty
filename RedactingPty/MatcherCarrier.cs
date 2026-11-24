using System.Collections;

namespace RedactingPty;

/// <summary>
/// Carries <see cref="RedactionOptions"/> to a runner's 1.0 <c>Run(argv, replacements)</c> inside the
/// replacement list, so the options overloads reuse that method (UnixPty's <c>Run</c> holds the
/// fork-safe child path; its signature is frozen, and its body reads the options' environment from
/// here before the fork, to build the child's environment block). It enumerates as the plain
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
        ChildEnvironment.Validate(options.Environment);
        // Every runner's options overload comes through here, so WindowsArguments is refused off Windows by
        // all of them. The message never holds the text.
        if (options.WindowsArguments is not null && !OperatingSystem.IsWindows())
            throw new ArgumentException("RedactionOptions.WindowsArguments is supported on Windows only.", nameof(options));
        return new MatcherCarrier(replacements, options);
    }

    public StreamReplacement this[int index] => _replacements[index];

    public int Count => _replacements.Count;

    public IEnumerator<StreamReplacement> GetEnumerator() => _replacements.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

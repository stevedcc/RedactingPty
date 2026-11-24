using System.Text;

namespace RedactingPty;

/// <summary>
/// The Windows command line <see cref="WindowsPty"/> builds from argv, for Microsoft's C runtime
/// (<c>CommandLineToArgvW</c>). No platform attribute: it is plain string work, so the cross-platform
/// unit tests call it directly.
/// </summary>
internal static class WindowsCommandLine
{
    /// <summary>
    /// Builds a Windows command line string from <paramref name="argv"/> using the quoting
    /// rules required by <c>CommandLineToArgvW</c>: each argument that is empty or contains
    /// any whitespace (<see cref="char.IsWhiteSpace(char)"/>) or a double quote, as .NET's
    /// <c>ProcessStartInfo.ArgumentList</c>, is wrapped in double-quotes; embedded double-quotes are escaped as
    /// <c>\"</c>; backslashes immediately before a double-quote (or the closing quote) are
    /// doubled. Arguments that need no quoting are appended verbatim.
    /// </summary>
    internal static string Build(IReadOnlyList<string> argv)
    {
        var parts = new string[argv.Count];
        for (int i = 0; i < argv.Count; i++)
            parts[i] = QuoteArgument(argv[i]);
        return string.Join(' ', parts);
    }

    /// <summary>
    /// <see cref="RedactionOptions.WindowsArguments"/>' rules for the Windows runners, beside their
    /// <c>argv[0]</c> check: with the text set, <paramref name="argv"/> must have one element, and the text
    /// must hold no NUL. The messages never hold the text.
    /// </summary>
    internal static void ValidateArguments(string? windowsArguments, string[] argv)
    {
        if (windowsArguments is null)
            return;
        if (argv.Length > 1)
            throw new ArgumentException(
                "RedactionOptions.WindowsArguments is set and argv has more than one element: give the arguments in one form only.", nameof(argv));
        if (windowsArguments.Contains('\0'))
            throw new ArgumentException("RedactionOptions.WindowsArguments contains NUL.", "options");
    }

    /// <summary>One argument as <see cref="Build"/> writes it.</summary>
    internal static string QuoteArgument(string argument)
    {
        // Empty arg must be quoted so it survives as an empty token. The rule is .NET's
        // (dotnet/runtime PasteArguments.cs): any char.IsWhiteSpace character or a double quote.
        if (argument.Length > 0 && !NeedsQuotes(argument))
            return argument;

        var sb = new StringBuilder("\"");
        int backslashes = 0;
        foreach (char c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
            }
            else if (c == '"')
            {
                // Each preceding backslash plus the quote itself must be escaped.
                sb.Append('\\', backslashes * 2 + 1);
                sb.Append('"');
                backslashes = 0;
            }
            else
            {
                if (backslashes > 0) { sb.Append('\\', backslashes); backslashes = 0; }
                sb.Append(c);
            }
        }
        // Trailing backslashes before the closing quote must be doubled.
        sb.Append('\\', backslashes * 2);
        sb.Append('"');
        return sb.ToString();
    }

    private static bool NeedsQuotes(string argument)
    {
        foreach (char c in argument)
        {
            if (char.IsWhiteSpace(c) || c == '"')
                return true;
        }
        return false;
    }
}

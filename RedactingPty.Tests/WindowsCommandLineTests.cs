using Xunit;

namespace RedactingPty.Tests;

/// <summary>
/// <see cref="WindowsCommandLine.Build"/>, in process (any OS): an argument is quoted when it is empty or
/// holds <c>"</c> or any <see cref="char.IsWhiteSpace(char)"/> character, as .NET's
/// <c>ProcessStartInfo.ArgumentList</c> does (dotnet/runtime <c>PasteArguments.cs</c>).
/// </summary>
public class WindowsCommandLineTests
{
    /// <summary>The 25 characters for which <see cref="char.IsWhiteSpace(char)"/> is true (.NET 10 and 11).</summary>
    internal static readonly char[] WhiteSpaceChars =
    [
        '\u0009', '\u000A', '\u000B', '\u000C', '\u000D', '\u0020', '\u0085', '\u00A0', '\u1680',
        '\u2000', '\u2001', '\u2002', '\u2003', '\u2004', '\u2005', '\u2006', '\u2007', '\u2008',
        '\u2009', '\u200A', '\u2028', '\u2029', '\u202F', '\u205F', '\u3000',
    ];

    public static TheoryData<char> WhiteSpace => new(WhiteSpaceChars);

    [Fact]
    public void WhiteSpace_IsExactlyTheCharactersCharIsWhiteSpaceAccepts()
    {
        var all = Enumerable.Range(0, 0x10000).Select(c => (char)c).Where(char.IsWhiteSpace);

        Assert.Equal(WhiteSpaceChars, all);
    }

    [Fact]
    public void PlainArgument_Unquoted() => Assert.Equal("p ab", WindowsCommandLine.Build(["p", "ab"]));

    [Fact]
    public void EmptyArgument_Quoted() => Assert.Equal("p \"\"", WindowsCommandLine.Build(["p", ""]));

    [Fact]
    public void Space_Quoted() => Assert.Equal("p \"a b\"", WindowsCommandLine.Build(["p", "a b"]));

    [Theory]
    [MemberData(nameof(WhiteSpace))]
    public void AnyWhiteSpace_Quoted(char c) =>
        Assert.Equal("p \"a" + c + "b\"", WindowsCommandLine.Build(["p", "a" + c + "b"]));

    [Fact]
    public void DoubleQuote_QuotedAndEscaped() => Assert.Equal("p \"a\\\"b\"", WindowsCommandLine.Build(["p", "a\"b"]));

    [Fact]
    public void Backslash_NoWhiteSpace_UnquotedUnchanged() => Assert.Equal("p a\\b", WindowsCommandLine.Build(["p", "a\\b"]));

    [Fact]
    public void TrailingBackslash_Quoted_Doubled() => Assert.Equal("p \"a b\\\\\"", WindowsCommandLine.Build(["p", "a b\\"]));

    [Fact]
    public void BackslashBeforeQuote_Doubled_QuoteEscaped() =>
        Assert.Equal("p \"a\\\\\\\"b\"", WindowsCommandLine.Build(["p", "a\\\"b"]));

    [Fact]
    public void ZeroWidthSpace_IsNotWhiteSpace_Unquoted() => Assert.Equal("p a\u200Bb", WindowsCommandLine.Build(["p", "a\u200Bb"]));

    [Fact]
    public void ProgramWithSpace_Quoted() =>
        Assert.Equal("\"C:\\Program Files\\x.exe\" y", WindowsCommandLine.Build(["C:\\Program Files\\x.exe", "y"]));
}

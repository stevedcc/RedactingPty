# Brief R4: a caller-built Windows command line, and `WindowsPty` quoting like .NET (1.3.0)

RedactingPty (github stevedcc/RedactingPty, NuGet `RedactingPty`, MIT) runs a child process in a
pseudo-terminal and redacts secrets from its output as it streams. tswap's `run` uses it. It targets
`net10.0` (LTS, as a library stays), is `IsAotCompatible`, and has no dependencies. Base: `origin/main` at
2848746 (1.2.0); branch `brief/r4`. Consumer: tswap brief T55 (tswap #402, Git Bash on Windows), which
needs this release before its Task 4. Read `README.md` ("Usage", "Setting the child's environment (1.2)",
"Platform support", "Testing"), `docs/briefs/r3-child-environment.md` (the last change of this shape),
`RedactingPty/RedactionOptions.cs`, `RedactingPty/IPtyRunner.cs`, `RedactingPty/MatcherCarrier.cs`,
`RedactingPty/WindowsPty.cs`, `RedactingPty/FallbackPty.cs`, `RedactingPty.TestHost/Program.cs` and
`RedactingPty.Tests/PtyRunnerTests.Environment.cs` (the Windows tests and `BuildWindowsBlock`'s unit tests).

Rules: test first (watch each test fail for the right reason), one commit per task, `dotnet build
RedactingPty.slnx` warning-free and `dotnet test RedactingPty.slnx` green after each commit. No new
NuGet packages in the library. Never download or install an SDK or a toolchain (`export
DOTNET_ROOT=$HOME/dotnet PATH=$HOME/dotnet:$PATH`). Nothing needs `sudo`. Always build from clean when
switching between a JIT and a NativeAOT test host. Commits end with
`Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`, explicit paths, no push, merge or rebase.
A break-it-once check per key test, named in the commit message. Never change an existing test's
assertion; this brief changes none (it adds tests, a test-host option and a test-child mode). The
Windows tests can't run here: CI runs them (Windows, JIT Debug, JIT Release and NativeAOT hosts); say so
in each commit message that adds one. Stop and report when the brief can't be built as written. No test
failure text or exception message contains a test's argument text beyond the fixed markers below.

## Why

1. **A caller that must build the command line itself.** On Windows a child gets one command-line
   string, and each program parses it by its own rules. Both runners build it for Microsoft's C runtime
   (`CommandLineToArgvW`): `WindowsPty` with its own `BuildWindowsCommandLine` (`WindowsPty.cs:228`,
   `402-450`), `FallbackPty` through `ProcessStartInfo.ArgumentList` (`FallbackPty.cs:46-47`). Programs
   with other rules get a different argv: MSYS2 programs (Git Bash) remove quotes and expand braces,
   globs and `@file` in unquoted words, and treat `\\` inside double quotes as an escape (tswap T55 cites
   the msys2-runtime source). tswap T55 quotes for MSYS2 itself, so it needs a way to pass its own text.
2. **The two Windows runners build different command lines.** `WindowsPty` quotes an argument only when
   it is empty or holds a space, tab or `"` (`QuoteNeededChars`, `WindowsPty.cs:417`); .NET quotes when
   it is empty or holds any `char.IsWhiteSpace` character or `"` (dotnet/runtime `release/10.0`
   cb8d459, `PasteArguments.cs:23` and `81-93`). An argument holding LF reaches a ConPTY child unquoted
   and a redirected one quoted. A C-runtime child parses both the same, but MSYS2 ends a word at CR and
   LF too (msys2-runtime `winsup.h:137`, `issep` is `" \t\n\r"`), so tswap #402's line-break values arrive
   cut on the ConPTY leg only.

## Task 1: `WindowsPty` quotes like .NET

`QuoteNeededChars` and the `IndexOfAny` test in `WindowsQuoteArg` are replaced by .NET's rule: quote when
the argument is empty, or holds `"`, or holds a character for which `char.IsWhiteSpace` is true. That is
exactly these 25 (checked by enumerating `char.IsWhiteSpace` over U+0000-U+FFFF on .NET 11.0.0; .NET 10
has the same set): U+0009-U+000D (tab, LF, vertical tab, form feed, CR), U+0020, U+0085, U+00A0, U+1680,
U+2000-U+200A, U+2028, U+2029, U+202F, U+205F, U+3000. The escaping inside the quotes is unchanged (it
already matches `PasteArguments.cs:30-77`: backslashes doubled before a `"` and before the closing quote,
`"` as `\"`). Use `char.IsWhiteSpace` itself, not a copied list. `argv[0]` goes through the same function
as today (CreateProcess reads a quoted module name the same way). Update `BuildWindowsCommandLine`'s doc
comment ("spaces, tabs, or double-quotes" becomes "any whitespace (`char.IsWhiteSpace`) or a double quote,
as .NET's `ProcessStartInfo.ArgumentList`").

Make `BuildWindowsCommandLine` `internal` and unit-test it cross-platform (`[Fact]`,
`WindowsCommandLineTests`), as R3 did for `BuildWindowsBlock`:

| argv (C# literals) | Command line |
|---|---|
| `["p", "ab"]` | `p ab` |
| `["p", ""]` | `p ""` |
| `["p", "a b"]` | `p "a b"` |
| `["p", "a\u0009b"]` ... one row per whitespace character above, in `"a" + c + "b"` | `p "a` + c + `b"` |
| `["p", "a\"b"]` | `p "a\"b"` |
| `["p", "a\\b"]` | `p a\b` (no whitespace: unquoted, unchanged) |
| `["p", "a b\\"]` | `p "a b\\"` (trailing backslash doubled) |
| `["p", "a\\\"b"]` | `p "a\\\"b"` |
| `["p", "a\u200Bb"]` (zero-width space, not whitespace) | `p a\u200Bb` |
| `["C:\\Program Files\\x.exe", "y"]` | `"C:\Program Files\x.exe" y` |

(Right column: the command line's own characters.) Break-it-once: put back `[' ', '\t', '"']`; the LF,
CR, vertical-tab and form-feed rows must fail.

On Windows CI (`[WindowsPtyFact]`), through the host: a new test-child mode (below) records the command
line it was started with. For each argv `[child, "a" + c + "b"]` (each of the 25 characters) and
`["x y", "a\"b", "a b\\", ""]`, run the child once with `windows` and once with `fallback`; assert the
part after the program token is identical for both runners, and that `CommandLineToArgvW` (shell32,
P/Invoke in the test) of each recorded line gives back exactly the child's arguments. Break-it-once:
the same revert; the LF case of this test must fail (on CI, the controller's).

**Test child** `RedactingPty.TestHost child cmdline FILE [ARG]...` (Windows only; elsewhere exit 121):
writes `GetCommandLineW()` (kernel32, `LibraryImport`, AOT-safe) to `FILE` as UTF-8 without BOM and exits
0. It ignores its other arguments. Extend `args is ["child", var mode]` to allow this form; the existing
modes stay as they are. The file, not the PTY output, carries the result, so ConPTY's rendering can't
change it.

Also clear the `cmdLine` buffer (`Array.Clear`) once `CreateProcess` has returned, whether it succeeded
or not: it holds the child's arguments, which may be secrets (the `cmdStr` string can't be cleared; say
so in the comment, as for the environment).

## Task 2: `RedactionOptions.WindowsArguments`

```csharp
/// <summary>
/// Windows only: the command line's arguments as the caller built them, passed verbatim after the
/// program instead of argv[1..] quoted by the runner. Null: argv as before.
/// </summary>
public string? WindowsArguments { get; init; }
```

**The contract** (the XML `<remarks>` and the README say all of it):

- The command line the child gets is the program token, then, when `WindowsArguments` is not empty, one
  space and the text unchanged. Empty: the program token alone. The runners never add, remove, quote or
  escape anything in the text.
- The program token is the runner's, as today: `WindowsPty` writes `argv[0]` through
  `WindowsQuoteArg` (quoted only when it holds whitespace or `"`, or is empty); `FallbackPty` gives
  `argv[0]` to `ProcessStartInfo.FileName` with `Arguments = WindowsArguments`, and .NET trims it and
  wraps it in `"` unless it is already quoted (dotnet/runtime `Process.Windows.cs:670-691`), then adds a
  space and `Arguments` when not empty (`ProcessStartInfo.cs:207-215`). So the two runners may differ in
  the program token's quotes only; a program that parses its own name by other rules is the caller's
  concern. The program is found as today (`CreateProcess` with no application name).
- **Validation** (`ArgumentException`, nothing started): `WindowsArguments` set on any OS but Windows (in
  `MatcherCarrier.For`, so every runner's options overload checks it, `LinuxPty`, `MacOSPty` and
  `FallbackPty` alike); set while `argv` has more than one element (in `WindowsPty.Run` and
  `FallbackPty.Run`, beside the existing `argv[0]` check: the caller must choose one form); holding NUL
  (`CreateProcess` would cut the line). CR, LF and every other character are allowed: inside quotes they
  are ordinary for many parsers.
- **`ToString`:** the record's generated `ToString` would print the text, which may hold secrets. Add a
  `PrintMembers` override (sealed record: `private bool PrintMembers(StringBuilder builder)`) that
  prints every public property as the generated one does, except `WindowsArguments`, shown as
  `WindowsArguments = <N characters>` or `WindowsArguments = null`. Equality stays the generated
  (ordinal) one.
- **`IPtyRunner`'s default overload** (`IPtyRunner.cs:31-37`) delegates only when, in addition to the
  current conditions, `WindowsArguments` is null, and otherwise throws `NotSupportedException`; its XML doc
  names `WindowsArguments` too.
- `WindowsPty` builds `BuildWindowsCommandLine([argv[0]])`, then `" " + WindowsArguments` when not empty,
  into the same buffer it clears after `CreateProcess` (Task 1). `FallbackPty` sets `Arguments` and adds
  nothing to `ArgumentList`.

**Test host** option `--windows-arguments-file FILE`: `WindowsArguments` from `FILE`, read as UTF-8, the
whole content unchanged (no trimming; an empty file is the empty string, distinct from no option); the
text never goes in the host's own argv. It runs the options overload; with only it (or only it and the
R3 options) `Matchers` is empty, as `--env-file` alone does.

Tests:

- `[WindowsPtyFact]`, `windows` and `fallback` runners: argv `[HostPath]`, `WindowsArguments`
  `child cmdline <file> "a b" plain "q\"x" "line` + LF + `break"` (a file in the test's temp folder, no
  spaces in its path): the recorded command line is the program token, one space and exactly that text
  (byte for byte), and the program token is `HostPath` or `"HostPath"`. `WindowsArguments = ""` with argv
  `[HostPath]`: the child is the host with no arguments, which exits 121 (usage); the runner returns 121
  (the program token alone reached the child).
- `[WindowsPtyFact]`: argv `[cmd, "/c", "echo x>marker"]` with `WindowsArguments = "x"`, and
  `WindowsArguments` holding NUL with argv `[cmd]`: `ArgumentException` from both runners, the marker
  absent (the R3 pattern, `PtyRunnerTests.Environment.cs:459-470`).
- In process, `[Fact]` on Linux and macOS: each built-in runner for this OS and `FallbackPty`, given
  `WindowsArguments = "x"` with an argv that creates a marker (`sh -c`): `ArgumentException`, marker
  absent.
- `[Fact]`: `new RedactionOptions { WindowsArguments = "secret-marker-7Hq" }.ToString()` does not contain
  `secret-marker-7Hq` and contains `WindowsArguments = <17 characters>`; with null it contains
  `WindowsArguments = null`; it still names `Matchers`, `ReleaseWhenAwaitingInput` and `Environment`.
- Beside the `CustomRunner_*` tests (which stay unchanged): `CustomRunner_DefaultOverload_WithWindowsArgumentsThrows`
  (`NotSupportedException`, the two-argument `Run` never called).

Break-it-once: (1) `WindowsPty` ignores `WindowsArguments`: the recorded-line test fails on CI (the
controller's); (2) `FallbackPty` adds it to `ArgumentList` instead of `Arguments`: the same test fails for
`fallback` (the text arrives quoted); (3) drop the `PrintMembers` override: the `ToString` test fails;
(4) drop the default overload's new condition: `CustomRunner_DefaultOverload_WithWindowsArgumentsThrows`
fails; (5) drop the non-Windows check: the Linux test fails here.

## Task 3: docs and the version

- README, under Usage: "### Passing a Windows command line yourself (1.3)": what `WindowsArguments` does,
  the contract above (verbatim tail, the runner's program token, `argv` of one element, NUL refused,
  Windows only, not in `ToString`), when to use it (a child that doesn't parse its command line by the C
  runtime's rules, such as an MSYS2 program; the caller then owns the quoting), and a short example. In
  "Platform support", one sentence: both Windows runners quote an argument holding any whitespace or `"`,
  as .NET's `ProcessStartInfo.ArgumentList` does (since 1.3; before, `WindowsPty` quoted only for space,
  tab and `"`). In the matchers paragraph, the `NotSupportedException` sentence names `WindowsArguments`
  too.
- `RedactingPty.csproj`: `PackageValidationBaselineVersion` to 1.2.0 (the API only grows), the local
  `<Version>` placeholder to `1.3.0-local`, and the comments that name the baseline say 1.2.0. The release
  is tag `v1.3.0` (the publish workflow takes the version from the tag); the controller tags it.
- There is no changelog file (the README's section headings carry the version, as for 1.1 and 1.2); in
  the report, give the release-note text: "1.3.0: `RedactionOptions.WindowsArguments` passes a caller-built
  Windows command line unchanged; `WindowsPty` quotes an argument holding any whitespace (CR, LF, vertical
  tab, form feed, Unicode spaces), as .NET does, so both Windows runners build the same command line."

## Out of scope

- Quoting rules for any particular program (MSYS2's is tswap T55's); a Unix counterpart (argv is passed
  as is there); quoting `argv[0]` the same way in both runners; the .NET 11 test targets (R2).

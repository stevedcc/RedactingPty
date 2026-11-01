# Brief R1: RedactingPty in idiomatic .NET 10 / C# 14, behind real PTY tests (behaviour unchanged)

RedactingPty (github stevedcc/RedactingPty, NuGet `RedactingPty`, MIT) runs a child process in a
pseudo-terminal and redacts secrets from its output as it streams. tswap's `run` uses it
(`TswapCli.csproj`: `RedactingPty` 1.0.0). It targets `net10.0`, is `IsAotCompatible`, and has no
dependencies.

Rules: one commit per item, `dotnet build RedactingPty.slnx` warning-free and
`dotnet test RedactingPty.slnx` green after each commit. No new NuGet packages in the library.
Never download or install an SDK or a toolchain (the SDK `global.json` names, 10.0.302 with
`rollForward: latestFeature`, is installed at `~/dotnet`; `export DOTNET_ROOT=$HOME/dotnet
PATH=$HOME/dotnet:$PATH` if `dotnet --version` is not 10.0.x or 11.0.x). Nothing in this brief
needs `sudo`.

## Why

**Order:** first of two briefs. R2 retargets to .NET 11 and is held until .NET 11 GA.
Owner (2026-11-01): "Let's do an upgrade branch for redactingpty too. First of all idiomatic net
10, then net 11." It mirrors tswap's T14/T15 pair.

The library is small (about 1000 production lines in 9 files) but delicate: `UnixPty` forks, and
the child must make only async-signal-safe native calls with no managed allocation until `execvp`
or `_exit`; `WindowsPty` drives ConPTY. **Neither real-PTY runner has a test today.** The only
tests are `StreamRedactorTests` (20) and `FallbackPtyTests` (7, in-memory `Drain`). A refactor of
the P/Invoke layer with no test that runs a child in a PTY would be blind, so item 1 comes first.

Counts on `main` (`e845846`), production code (`RedactingPty/`):

| Older form | Count |
|---|---|
| `[DllImport]` (UnixPty 8, LinuxPty 2, MacOSPty 1, WindowsPty 12) | 23 (no `[LibraryImport]`) |
| `IntPtr` (`nint` used 6 times) | 68 |
| `new object()` as a lock (`FallbackPty.cs`) | 1 |
| `throw new Exception(...)` | 10 |
| bare `catch { }` | 5 |
| `(IPtyRunner)` cast in a ternary chain (`PtyRunnerFactory.Create`) | 1 |
| `Thread.Sleep(200)` polling loop (`WindowsPty` drain) | 1 |

## Rules for the whole brief

- **Behaviour doesn't change.** Use the `csharp-refactoring` skill's discipline
  (`/home/stevecrawford/RiderProjects/croft/.claude/skills/csharp-refactoring/SKILL.md`) and
  `/home/stevecrawford/RiderProjects/croft/.claude/skills/writing-dotnet10-code/SKILL.md` (its
  defaults table and "Always" line; its Croft-only rows, EF Core and Blazor, don't apply).
- **Public API unchanged:** `IPtyRunner`, `PtyRunnerFactory.Create()`, `StreamRedactor`
  (constructor, `ProcessChunk`, `Flush`), `StreamReplacement`. Check with the package's public
  surface before and after (e.g. compare the `ref`/XML-doc output, or list public types and
  members by reflection) and put the comparison in the report.
- **Exception types and messages unchanged** (callers may catch them): the ten
  `throw new Exception(...)` stay `Exception` with the same text. Item 6 reviews them, report only.
- **Fork safety is the hard constraint.** Code that runs in the child between `forkpty` returning
  0 and `execvp`/`_exit` (`UnixPty.Run`'s `if (pid == 0)` block, and every P/Invoke it calls:
  `execvp_native`, `write_ptr`, `_exit`) must, after the refactor, still allocate nothing, take no
  lock, not touch the GC heap and not marshal. If you aren't certain a changed form keeps that,
  leave it as it is and say why.
- Keep AOT clean: `dotnet publish` of a tiny NativeAOT console app that references the library
  (item 1's test host is fine for that) reports 0 AOT/trim warnings, before and after.
- Tests change only where item 1 adds them. Never change an existing test's assertion.

## Items

### Item 1: characterization tests for the real PTY runners (first, on unchanged code)

Add tests that run a real child process through `LinuxPty`, `MacOSPty` and `WindowsPty` and assert
what a user sees: the redacted output and the child's exit code. They must pass on the unchanged
code, in CI on all three OSes, before any refactor commit.

Suggested design (adapt if something doesn't work; say what you changed and why):
- A small non-packable console project, e.g. `RedactingPty.TestHost`, referenced only by the tests.
  It takes a runner name (`linux`, `macos`, `windows`), replacements and argv, constructs that
  runner **directly** (bypassing `PtyRunnerFactory`'s redirection check: the runners are
  `internal`, so give the test host `InternalsVisibleTo`), runs it and exits with its exit code.
  The runners write to the process's stdout, so the test can launch the host with stdout and
  stderr redirected and read what the runner wrote.
- Tests (per OS, skipped on the others):
  - a secret printed by the child comes out replaced, and text around it unchanged;
  - a secret split across two writes of the child (e.g. two `printf`s with a `sleep 0.1`
    between them, or the Windows equivalent) still comes out replaced: the stream redactor's
    overlap through the real read loop;
  - the child's exit code passes through (e.g. `sh -c 'exit 7'` → 7; Windows `cmd /c exit 7`);
  - Unix: a child killed by a signal returns 128 + signal (e.g. `sh -c 'kill -TERM $$'` → 143);
  - Unix: the child really has a TTY (e.g. `sh -c 'test -t 1 && echo is-a-tty'` prints
    `is-a-tty`), which is the point of a PTY runner over `FallbackPty`;
  - Unix: a command that doesn't exist exits 127 and prints
    `redactingpty: exec failed (command not found or not executable)` (the child-path diagnostic).
- If the CI runners can't host one of these (e.g. ConPTY without a console), stop and report with
  the evidence; don't skip it silently.
- Break-it-once for each OS's redaction test: make that runner write `ProcessChunk`'s input instead
  of its output; the test fails. Revert. Name it in the commit message.

The controller pushes the branch and opens the PR; your report marks the macOS and Windows runs as
"pending the PR run", and the controller fills them in from the check log. You never state a CI
result you haven't seen.

### Item 2: `LibraryImport` instead of `DllImport` (23 sites)

`[LibraryImport]` with `static partial`, keeping each entry point name, library name and
`SetLastError` exactly as today.
- `bool` returns of Win32 functions → `[return: MarshalAs(UnmanagedType.Bool)]` (4-byte BOOL, as
  `DllImport`'s default marshalling was). `CreateProcess`'s `CharSet.Unicode` strings →
  `StringMarshalling = StringMarshalling.Utf16`.
- `[Out] byte[]` / `[In] byte[]` buffers (`read`, `write`, `ReadFile`, `WriteFile`): check in the
  generated source under `obj/` that they are pinned and passed in place, not copied (a copy would
  change what a short write sends). State it in the report with the generated line.
- **The child-path imports** (`execvp_native`, `write_ptr`, `_exit`): read their generated stubs.
  Convert them only if the stub is a direct call with no marshalling, no allocation and no
  last-error handling (none of them sets `SetLastError` today). Quote the generated stub of each
  in the report. If any stub does more, keep that one as `DllImport` and say why.
- `LinuxPty`'s `libc`/`libutil` probe and fallback stay exactly as they are.

### Item 3: `nint` for native handles and sizes (68 `IntPtr`)

`IntPtr` → `nint`, `IntPtr.Zero` → `0`, `new IntPtr(x)` → `(nint)x`, `IntPtr.Size` → `nint.Size`,
where it is a pointer, handle or size in a P/Invoke signature, struct field or local. Identical
type (`nint` is `IntPtr`), so this is purely spelling; do it after item 2 so the signatures are
touched once more at most.

### Item 4: smaller idioms

- `FallbackPty`'s `var sharedLock = ... new object() : null` and `lock (writeLock)`:
  `System.Threading.Lock` (the `Drain` parameter type changes from `object?` to `Lock?`; it's
  `internal`, update `FallbackPtyTests` mechanically and say so).
- `PtyRunnerFactory.Create`: the `if`/ternary chain → one switch expression over the OS and
  `usePty` (e.g. a tuple or property pattern), with the same order of checks and the same result
  for every OS and redirection combination. Keep the comments that explain the fallback rules.
- Collection expressions, `is null`, and C# 14 forms (`field`, `extension` members) where they fit
  the `writing-dotnet10-code` defaults table; never forced. Report "none" where there are no sites.
- `WindowsPty`'s partial-write copy (`Buffer.BlockCopy` into a fresh `byte[]` per retry) and
  `UnixPty`'s equivalent: leave them as they are in this brief (a `Span` slice would need a
  pointer-taking import; that's a design change, not a refactor). Mention them in the report.

### Item 5: review only, no code

One line each in the report, with a proposal where you have one:
- the five bare `catch { }` (console size probes ×2, the stdin forwarding threads ×2, and
  `UnixPty`'s `readTask.Wait()` after a `waitpid` failure): why it catches everything, or what it
  should catch;
- the ten `throw new Exception(...)`: whether a specific type (e.g. `Win32Exception`,
  `IOException`) would be better, and what it would break for callers;
- `WindowsPty`'s `Thread.Sleep(200)` drain loop and `UnixPty`'s 200 ms poll timeout: whether
  either should change;
- the test project's floating versions (`xunit` `2.*`, `Microsoft.NET.Test.Sdk` `17.*`) and
  xunit v2 vs v3 (tswap uses xunit v3): a proposal only; Renovate is coming (tswap #218).

## Out of scope

- Anything .NET 11 / C# 15 offers (that's R2).
- Changing behaviour: timeouts, buffer sizes, exception types, the fallback rules, the drain
  truncation marker.
- Publishing: no tag, no NuGet release. The owner decides the version (proposal: 1.0.1, no API
  change) after merge.
- tswap itself.

## Report

`~/RiderProjects/redactingpty-wt/r1-report.md`: per item what changed (files, counts, sites left
alone and why), the generated-stub evidence from item 2, the public-surface comparison, the AOT
publish result before and after, the item 5 review, and the final `dotnet test` summary line.
State is kept in `~/RiderProjects/redactingpty-wt/r1-state.md` so the work can resume from disk.

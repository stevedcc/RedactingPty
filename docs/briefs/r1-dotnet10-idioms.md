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
| `IntPtr` as a type token in code (comments and `Marshal.WriteIntPtr` not counted; `nint` appears 9 times) | 83 |
| `new object()` as a lock (`FallbackPty.cs`) | 1 |
| `throw new Exception(...)` | 10 |
| catch-all `catch { ... }` (no exception type) | 5 |
| `(IPtyRunner)` cast in a ternary chain (`PtyRunnerFactory.Create`) | 1 |
| `Thread.Sleep(200)` polling loop (`WindowsPty` drain) | 1 |

## Rules for the whole brief

- **Behaviour doesn't change.** Use the `csharp-refactoring` skill's discipline
  (`/home/stevecrawford/RiderProjects/croft/.claude/skills/csharp-refactoring/SKILL.md`) and
  `/home/stevecrawford/RiderProjects/croft/.claude/skills/writing-dotnet10-code/SKILL.md` (its
  defaults table and "Always" line; its Croft-only rows, EF Core and Blazor, don't apply).
- **Public API unchanged:** `IPtyRunner`, `PtyRunnerFactory.Create()`, `StreamRedactor`
  (constructor, `ProcessChunk`, `Flush`), `StreamReplacement`. Package validation (below) enforces
  it against the published 1.0.0.
- **Exception types and messages unchanged** (callers may catch them): the ten
  `throw new Exception(...)` stay `Exception` with the same text. Item 5 reviews them, report only.
- **Fork safety is the hard constraint.** The child block (`UnixPty.Run`'s `if (pid == 0)`
  branch, between `forkpty` returning 0 and `execvp`/`_exit`) **keeps exactly the calls it makes
  today, no more**: `execvp_native`, `ExecFailedMsgPin.AddrOfPinnedObject()`, `ExecFailedMsg.Length`,
  `write_ptr`, `_exit`. (`AddrOfPinnedObject()` is a managed call after fork; that is pre-existing
  and low-risk. Don't "fix" it here; item 5 may propose hoisting it into a pre-fork local.) The
  P/Invokes it calls must not gain marshalling, allocation or last-error handling. Note that the
  parent never calls `execvp_native`, so its stub is first compiled in the child, today and after
  the change; that is why item 2 requires the generated stub as evidence. If you aren't certain a
  changed form is equivalent, leave it as it is and say why.
- Keep AOT clean: `dotnet publish RedactingPty.TestHost -c Release -r linux-x64 -p:PublishAot=true`
  (item 1's test host) reports 0 AOT/trim warnings, first right after item 1 ("before") and again
  at the end ("after"). Restoring the `Microsoft.DotNet.ILCompiler` NuGet package for that is a
  package restore and is allowed (it is already in `~/.nuget/packages`; clang is installed); it is
  not a toolchain install.
- **Public API guard:** item 1 turns on the SDK's package validation against the published
  package: `<EnablePackageValidation>true</EnablePackageValidation>` and
  `<PackageValidationBaselineVersion>1.0.0</PackageValidationBaselineVersion>` in the library
  csproj (built into the SDK, no new package; restoring the 1.0.0 baseline from nuget.org is
  allowed). From then on `dotnet pack` (CI's pack step runs it) fails on any public API change.
  If the validator rejects the csproj's `<Version>0.0.0-local</Version>` as lower than the
  baseline, set it to `1.0.1-local` (the publish workflow still overrides it from the tag).
- Tests change only where item 1 adds them. Never change an existing test's assertion.
- `writing-dotnet10-code`'s Discipline line "tests go through public behaviour: no
  `InternalsVisibleTo`" is **overridden here on purpose**: the library already grants
  `InternalsVisibleTo` to `RedactingPty.Tests`, and the real-PTY runners can't be reached through
  `PtyRunnerFactory` when stdout is redirected, so item 1's test host needs it too.

## Items

### Item 1: characterization tests for the real PTY runners (first, on unchanged code)

Add tests that run a real child process through `LinuxPty`, `MacOSPty` and `WindowsPty` and assert
what a user sees: the redacted output and the child's exit code. They must pass on the unchanged
code, in CI on all three OSes, before any refactor commit. The only production-file change in this
item is the library csproj (package validation, and `InternalsVisibleTo` for the test host).

**Design** (adapt if something doesn't work; say what you changed and why):
- A non-packable console project `RedactingPty.TestHost`, added to `RedactingPty.slnx`, referenced
  only by the tests. It takes a runner name (`linux`, `macos`, `windows`), replacements and argv,
  constructs that runner **directly** (bypassing `PtyRunnerFactory`'s redirection check), runs it
  and exits with its exit code. If `Run` throws, it prints the exception to stderr and exits with
  **120** (a code no test child uses), so a test fails with a diagnosis. It sets
  `Console.OutputEncoding = UTF8`.
- The tests launch the host with **stdin, stdout and stderr redirected**: stdin closed at once
  (empty), so the runner's stdin-forwarding thread sends nothing the PTY could echo; stdout and
  stderr read concurrently to EOF before waiting for exit (no pipe-buffer deadlock). Every launch
  has a timeout (30 s): on timeout, `Kill(entireProcessTree: true)` and fail with the output
  captured so far. Nothing may hang a CI job.
- **Assertions on what a user sees, robust to terminal framing.** A PTY's line discipline turns
  `\n` into `\r\n` (Unix), and ConPTY emits VT escape sequences and wraps at the 80-column size the
  runners fall back to. So: (1) the primary assertion is that the secret is **absent from the raw
  output bytes**; (2) then strip ANSI CSI/OSC sequences, normalise `\r\n` to `\n`, and assert the
  expected redacted text with `Contains`. Test strings are ASCII; on Windows every line is well
  under 80 columns.
- OS gating: xunit v2 has no runtime skip, so use a `Fact` subclass that sets `Skip` from
  `OperatingSystem.Is*()`. The controller checks each CI leg's log for the number of tests that ran
  (not skipped) on that OS.

**Tests:**
- all three OSes:
  - a secret printed by the child comes out replaced, the surrounding text unchanged;
  - **read-boundary coverage:** the child prints the secret on each of 300 short lines (well over
    4096 bytes in total, so some occurrences straddle the runners' 4096-byte reads): no occurrence
    leaks and the count of replacement markers is 300;
  - a secret as the **last bytes with no trailing newline** comes out replaced (the redactor's
    `Flush` tail);
  - the child's exit code passes through (`sh -c 'exit 7'` → 7; Windows `cmd /c exit 7` → 7).
- Unix only:
  - a child killed by a signal returns 128 + signal (`sh -c 'kill -TERM $$'` → 143);
  - the child really has a TTY (`sh -c 'test -t 1 && echo is-a-tty'` prints `is-a-tty`), which is
    the point of a PTY runner over `FallbackPty`;
  - stderr is merged and redacted too (`sh -c 'echo SECRET 1>&2'`);
  - a command that doesn't exist exits 127 and prints
    `redactingpty: exec failed (command not found or not executable)` (the child-path diagnostic);
  - best effort, documented as such: a secret split across two child writes
    (`sh -c 'printf "%s" "<4000 filler chars>hun"; sleep 0.3; printf "ter2 tail"'`) comes out
    replaced. If the writes coalesce into one read the test still passes, so it is not the
    required overlap check; the 300-line test is.
- If a CI runner can't host one of these, stop and report with the evidence; don't skip silently.

**Break-it-once**, one push that breaks all three runners at once (the controller reads the red
check runs, then you revert): (1) each runner writes `ProcessChunk`'s input instead of its output:
the redaction tests fail on every OS; (2) each runner returns 0 instead of the exit code: the exit
code tests fail; (3) Unix: the signal branch returns `status & 0x7f` without the 128: the signal
test fails; (4) Unix: the child writes nothing before `_exit(127)`: the diagnostic test fails.
Name them in the commit message.

The controller pushes the branch and opens the PR; your report marks the macOS and Windows runs as
"pending the PR run", and the controller fills them in from the check log. You never state a CI
result you haven't seen.

### Item 2: `LibraryImport` instead of `DllImport` (23 sites)

`[LibraryImport]` with `static partial` methods; the containing types (`UnixPty`, `LinuxPty`,
`MacOSPty`, `WindowsPty`) become `partial`. Keep each library name, entry point and `SetLastError`
exactly as today. Read the generated stubs with `-p:EmitCompilerGeneratedFiles=true` (under
`obj/.../generated/`); don't commit that setting. Add `<AllowUnsafeBlocks>` only if the compiler
asks for it, and say so.
- **`CreateProcess`:** today `CharSet.Unicode` makes `DllImport` resolve `CreateProcessW`;
  `LibraryImport` does no suffix probing, so set `EntryPoint = "CreateProcessW"` explicitly (a
  plain conversion would throw `EntryPointNotFoundException` on Windows). `string?` parameters
  get `StringMarshalling = StringMarshalling.Utf16`; the `bool bInheritHandles` parameter and the
  `bool` return get `[MarshalAs(UnmanagedType.Bool)]`. `[In] char[] lpCommandLine` must stay a
  buffer Windows can write to, passed in place: show in the generated stub that it is pinned, not
  copied. If any of this doesn't hold, keep `CreateProcess` as `DllImport` and say why.
- Every other `bool` return of a Win32 function → `[return: MarshalAs(UnmanagedType.Bool)]`
  (4-byte BOOL, as `DllImport`'s default was).
- **Structs passed to an import must stay layout-identical.** `SECURITY_ATTRIBUTES.bInheritHandle`
  is a `bool` field (non-blittable): change it to `int` (it is never set, so it is 0 either way,
  and the struct becomes blittable). For every struct passed to an import (`COORD`, `STARTUPINFO`,
  `STARTUPINFOEX`, `PROCESS_INFORMATION`, `SECURITY_ATTRIBUTES`, `Winsize`, `PollFd`), report
  `Marshal.SizeOf<T>()` before and after on x64 (e.g. from a throwaway test or the test host); all
  must be unchanged.
- `[Out] byte[]` / `[In] byte[]` buffers (`read`, `write`, `ReadFile`, `WriteFile`): show in the
  generated stub that they are pinned and passed in place, not copied.
- **The child-path imports** (`execvp_native`, `write_ptr`, `_exit`): convert them only if the
  generated stub is a direct call with no marshalling, no allocation and no last-error handling
  (none sets `SetLastError` today). Quote each generated stub in the report. If any does more,
  keep that one as `DllImport` and say why.
- `LinuxPty`'s `libc`/`libutil` probe and fallback stay exactly as they are.

### Item 3: `nint` for native handles and sizes (83 `IntPtr`)

`IntPtr` → `nint`, `IntPtr.Zero` → `0`, `new IntPtr(x)` → `(nint)x`, `IntPtr.Size` → `nint.Size`,
where it is a pointer, handle or size in a P/Invoke signature, struct field or local. Identical
type (`nint` is `IntPtr`), so this is purely spelling; do it after item 2 so the signatures are
touched once more at most. **Trap:** `var h = IntPtr.Zero;` must become `nint h = 0;`, not
`var h = 0;` (that infers `int`, and an `out nint` argument then fails to compile).
`Marshal.WriteIntPtr` is a method name and stays.

### Item 4: smaller idioms

- `FallbackPty`'s `var sharedLock = ... new object() : null` and `lock (writeLock)`:
  `System.Threading.Lock` (the `Drain` parameter type changes from `object?` to `Lock?`; it's
  `internal`; `FallbackPtyTests` passes `writeLock: null`, so change it only if the compiler asks).
- `PtyRunnerFactory.Create`: the `if`/ternary chain → one switch expression over the OS and
  `usePty` (e.g. a tuple or property pattern), with the same order of checks and the same result
  for every OS and redirection combination; the Windows arm keeps the
  `IsWindowsVersionAtLeast(10, 0, 17763)` gate, so older Windows still gets `FallbackPty`. Keep the
  comments that explain the fallback rules, and update the class doc comment ("add one `if`
  branch here").
- Collection expressions, `is null`, and C# 14 forms (`field`, `extension` members) where they fit
  the `writing-dotnet10-code` defaults table; never forced. Report "none" where there are no sites.
- `WindowsPty`'s partial-write copy (`Buffer.BlockCopy` into a fresh `byte[]` per retry) and
  `UnixPty`'s equivalent: leave them as they are in this brief (a `Span` slice would need a
  pointer-taking import; that's a design change, not a refactor). Mention them in the report.

### Item 5: review only, no code

One line each in the report, with a proposal where you have one:
- the five catch-all `catch` blocks (console size probes ×2, the stdin forwarding threads ×2, and
  `UnixPty`'s `readTask.Wait()` after a `waitpid` failure): why it catches everything, or what it
  should catch;
- `UnixPty`'s class comment: "full path bypasses PATH lookup" is wrong (`execvp` searches `PATH`),
  and "no CLR re-entry" is not quite true (`AddrOfPinnedObject()` runs in the child); propose the
  corrected wording and whether to hoist `AddrOfPinnedObject()` into a pre-fork local;
- the ten `throw new Exception(...)`: whether a specific type (e.g. `Win32Exception`,
  `IOException`) would be better, and what it would break for callers;
- `WindowsPty`'s `Thread.Sleep(200)` drain loop and `UnixPty`'s 200 ms poll timeout: whether
  either should change;
- the test project's floating versions (`xunit` `2.*`, `xunit.runner.visualstudio` `2.*`,
  `Microsoft.NET.Test.Sdk` `17.*`) and
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
alone and why), the generated-stub evidence and struct sizes from item 2, the package-validation
result (pack passes against the 1.0.0 baseline), the AOT publish result before and after, the item
5 review, and the final `dotnet test` summary line.
State is kept in `~/RiderProjects/redactingpty-wt/r1-state.md` so the work can resume from disk.

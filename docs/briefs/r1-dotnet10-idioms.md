# Brief R1: RedactingPty in idiomatic .NET 10 / C# 14 (behaviour unchanged)

RedactingPty (github stevedcc/RedactingPty, NuGet `RedactingPty`, MIT) runs a child process in a
pseudo-terminal and redacts secrets from its output as it streams. tswap's `run` uses it. It targets
`net10.0`, is `IsAotCompatible`, and has no dependencies. **Revised 2026-11-01 after brief R0**
(the fork-child fix, released as 1.0.2): R0 delivered this brief's former item 1 (the real-PTY
tests and the CI matrix) and changed the child path, so the rules below reflect the code on `main`
at `5d9a8fa`, which this branch now contains.

Rules: one commit per item, `dotnet build RedactingPty.slnx` warning-free and
`dotnet test RedactingPty.slnx` green after each commit. No new NuGet packages in the library.
Never download or install an SDK or a toolchain (`export DOTNET_ROOT=$HOME/dotnet
PATH=$HOME/dotnet:$PATH`; `global.json` rolls forward to the installed 10.0.4xx SDK). NuGet
restores are fine. Nothing in this brief needs `sudo`. Always build from clean when switching
between a JIT and a NativeAOT test host (a stale AOT host in `bin/` once gave a false pass).

## Why

**Order:** after R0, before R2 (.NET 11 test targets, held until GA). Owner (2026-11-01): "First of
all idiomatic net 10, then net 11." It mirrors tswap's T14/T15 pair.

Counts on `main` (`5d9a8fa`), production code (`RedactingPty/`):

| Older form | Count |
|---|---|
| `[DllImport]` (UnixPty 5: `read`, `write`, `close`, `waitpid`, `poll`; LinuxPty 2 and MacOSPty 1: `forkpty`, frozen; WindowsPty 12) | 20, of which 17 convertible (no `[LibraryImport]`) |
| `IntPtr` as a type token in code (comments and `Marshal.WriteIntPtr` not counted) | 80, of which 14 in frozen spans (`UnixPty` 93 ×2 and 167 ×2, `LinuxPty` 6, `MacOSPty` 4), so 66 convertible |
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
  (constructor, `ProcessChunk`, `Flush`), `StreamReplacement`. Package validation against the
  published 1.0.0 is already on (R0); CI's pack step fails on any public API change.
- **Exception types and messages unchanged** (callers may catch them): the ten
  `throw new Exception(...)` stay `Exception` with the same text. Item 5 reviews them, report only.
- **The fork-child path is frozen (R0's mechanism), spelling included.** These spans, as they are
  at `5d9a8fa`, stay byte-identical (no `IntPtr` → `nint`, no renames, no reformatting, no moved
  lines, no added or removed `using` they depend on):
  - `UnixPty.cs` 39–59: `ExecFailedMsg`, `ExecFailedMsgPin`, `Libc`, `ExecvpAddress`,
    `WriteAddress`, `ExitAddress`, and the `Winsize` struct;
  - `UnixPty.cs` 88–93: the `Forkpty` declaration and its doc comment;
  - `UnixPty.cs` 100–104: the `AggressiveOptimization` comment, attribute and `Run`'s signature;
  - `UnixPty.cs` 150–180: from `int pid;` to the closing brace of the `unsafe` block (the explicit
    `RuntimeHelpers.RunClassConstructor`, the locals `childExecvp`, `childWrite`, `childExit`,
    `execFailedMsg`, `execFailedMsgLength`, the `Forkpty` call and the `if (pid == 0)` branch);
  - `LinuxPty.cs` 14–34 and `MacOSPty.cs` 12–16: the `forkpty` imports, `LinuxPty`'s probe and both
    `Forkpty` overrides;
  - the library csproj's `<Optimize>true</Optimize>` and `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`.
  The argv marshalling before the fork (`UnixPty.cs` 118–133, `nativeStrings`/`nativeArgv`) is
  parent code and **not** frozen. README "Fork safety" explains why the rest is: unoptimised code
  routes even function-pointer calls through a stub built on first call, in the child, and any
  extra managed call there reintroduces the 1.0.0 crash. **Check, in the report:** extract each
  span at `5d9a8fa` and at your final commit by its first and last line's text (line numbers above
  may shift once earlier code changes) and show that `diff` prints nothing.
- **The tests from R0 are the safety net:** `PtyRunnerTests` (real child through each runner) and
  the CI matrix (Debug JIT, Release JIT, NativeAOT × ubuntu, macOS, Windows). ubuntu / jit-debug is
  the canary for code generated in the child. All nine legs stay green after every item; the
  controller pushes and reads them.
- Keep AOT clean: `dotnet publish RedactingPty.TestHost -c Release -r linux-x64 -p:PublishAot=true`
  reports 0 AOT/trim warnings before your first commit and at the end.
- Tests change only where a signature change forces it (items 2–4). Never change an existing
  test's assertion.
- If your counts differ from the table above, say so in the report before relying on them.
- `writing-dotnet10-code`'s Discipline line "tests go through public behaviour: no
  `InternalsVisibleTo`" stays overridden on purpose: the library grants `InternalsVisibleTo` to
  `RedactingPty.Tests` and `RedactingPty.TestHost`, because the real-PTY runners can't be reached
  through `PtyRunnerFactory` when stdout is redirected.

## Items

### Item 1: done in R0

The real-PTY characterization tests, the test host, the CI matrix and package validation are on
`main`. Nothing to do; this numbering is kept so the report lines up with the earlier review.

### Item 2: `LibraryImport` instead of `DllImport` (17 of 20; `forkpty` stays `DllImport`)

`[LibraryImport]` with `static partial` methods; the containing types (`UnixPty`, `LinuxPty`,
`MacOSPty`, `WindowsPty`) become `partial`. Keep each library name, entry point and `SetLastError`
exactly as today. Read the generated stubs with `-p:EmitCompilerGeneratedFiles=true` (under
`obj/.../generated/`); don't commit that setting. `<AllowUnsafeBlocks>` is already set (R0); keep
it.
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
- `[Out] byte[]` / `[In] byte[]` buffers (`ReadFile`, `WriteFile`): show in the generated stub
  that they are pinned and passed in place, not copied.
- **`forkpty`** (`LinuxPty` ×2, `MacOSPty`): frozen (rules). The child returns from `fork` through
  this import's stub and the `Forkpty` override; that return path is proven by R0's CI matrix and
  its gdb evidence. A `LibraryImport` wrapper would replace it with a new stub whose helpers must
  all be compiled before the fork, for no gain.
- `read`, `write`, `close`, `waitpid`, `poll` (`UnixPty`): parent-side only; convert them. Show in
  the generated stub that `read`'s `[Out] byte[]` and `write`'s `[In] byte[]` are pinned in place.
- `LinuxPty`'s `libc`/`libutil` probe and fallback stay exactly as they are.

### Item 3: `nint` for native handles and sizes (66 convertible `IntPtr`)

`IntPtr` → `nint`, `IntPtr.Zero` → `0`, `new IntPtr(x)` → `(nint)x`, `IntPtr.Size` → `nint.Size`,
where it is a pointer, handle or size in a P/Invoke signature, struct field or local. Identical
type (`nint` is `IntPtr`), so this is purely spelling; do it after item 2 so the signatures are
touched once more at most. **Trap:** `var h = IntPtr.Zero;` must become `nint h = 0;`, not
`var h = 0;` (that infers `int`, and an `out nint` argument then fails to compile).
`Marshal.WriteIntPtr` is a method name and stays. The frozen spans (rules) keep their spelling.

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
- **Redaction throughput (measure, don't change):** `StreamRedactor` scans each window once per
  replacement (`IndexOf` loop, then `string.Replace` per replacement). Measure throughput with 1,
  10 and 50 replacements over ~100 MB of mixed text (a throwaway benchmark, not committed), and
  say whether a single-pass multi-string search (`SearchValues<string>`) would be worth a later
  brief. (In tswap, `redact`'s per-line regex approach measured 2.1 s for 200 secrets × 221 KB
  against 9 ms single-pass, tswap #220; `run` typically passes 1–3 replacements.)
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
- Publishing: no tag, no NuGet release. The owner decides the version (proposal: 1.0.3, no API
  change) after merge.
- The fork-child path (rules), and the R0 follow-ups (RedactingPty #6 GC-suspension hang, #7
  `execvp` → `execve`).
- tswap itself.

## Report

`~/RiderProjects/redactingpty-wt/r1-report.md`: per item what changed (files, counts, sites left
alone and why), the generated-stub evidence and struct sizes from item 2, a statement that the
fork-child region is byte-identical to `main` (e.g. the diff of that region is empty), the
package-validation result, the AOT publish result before and after, the item 5 review with the
throughput numbers, and the final `dotnet test` summary line.
State is kept in `~/RiderProjects/redactingpty-wt/r1-state.md` so the work can resume from disk.

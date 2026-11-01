# Brief R0: the forked child must run no JIT-compiled code (bug fix, release 1.0.1)

Rules: TDD, one commit per item, `dotnet build RedactingPty.slnx` warning-free after each commit.
No new NuGet packages in the library, no native shim (the library stays dependency-free and
pure managed). Never download or install an SDK or toolchain (`export DOTNET_ROOT=$HOME/dotnet
PATH=$HOME/dotnet:$PATH`; NuGet restores are fine). No `sudo`. Don't push, tag or open PRs: the
controller does that and reports CI results back to you; never state a CI result you haven't seen.
Use the `systematic-debugging` discipline: root cause before fix.

## The bug (found by brief R1's implementer, 2026-11-01; reproduced by the controller)

On Linux, when the library runs on the JIT (any non-NativeAOT host: `dotnet run`, a normal app, a
Debug build, a test run) with W^X on (the default), `UnixPty`'s forked child crashes with SIGSEGV
whenever it triggers code generation after `fork`, so the runner returns 139 (in a Release build
that happens on the missing-command path; in Debug on every run):

| Build of the host | `sh -c 'exit 7'` (expect 7) | missing command (expect 127 + `redactingpty: exec failed ...`) |
|---|---|---|
| Debug, JIT | 139 | 139 |
| Debug, JIT, `DOTNET_EnableWriteXorExecute=0` | 7 | 127 + message |
| Release, JIT | 7 | 139, no message |
| NativeAOT | 7 | 127 + message |

Evidence so far: the child dies of signal 11 inside libcoreclr, called from JIT-compiled code; the
JIT log shows the parent never compiles the native-call stub for `execvp_native`, so the child
compiles it after `fork`. Turning off W^X fixes it; `Marshal.Prelink` before fork did not. tswap
is unaffected (it ships NativeAOT), but tswap's own E2E tests set `DOTNET_EnableWriteXorExecute=0`
with the comment "fork() from a JIT-mode .NET process crashes when W^X protection is active", so
the symptom was known and worked around in tests, never fixed. RedactingPty 1.0.0 ships the bug.

**Working hypothesis (confirm or refute it, with evidence):** with W^X the runtime writes JIT code
through a separate writable mapping of a shared memory object; after `fork` the child shares those
pages with the parent, and the runtime state that manages them (locks, allocator state held by
other parent threads at fork time) is not valid in the child. So any code generation in the child
is unsafe, and with W^X it crashes; without W^X it merely works by luck. (That describes Linux's
double-mapping W^X; record which mechanism is active on this box, e.g. from `/proc/<pid>/maps`.
macOS uses `MAP_JIT` instead; the fix must not depend on the mechanism.) If the hypothesis holds,
the child must execute **no code generated after fork** on any host: no IL stub, no JIT, no lazy
import resolution or precode patching, no type initializer, no managed allocation.

**A second trigger the reviews found:** no `UnixPty` static is touched in the parent before
`fork` (`EAGAIN` is read only after it; `ExecFailedMsg`/`ExecFailedMsgPin` only in the child
branch), so `UnixPty`'s static constructor (`u8.ToArray()`, `GCHandle.Alloc`, and the
constructor's own JIT compilation) first runs **in the child**. The known-correct pattern is the
runtime's own `Process.Start` on Unix: the child between fork and exec runs only native code (in
`System.Native`). With no native shim allowed, R0 approximates that invariant: everything the
child executes is compiled, resolved and initialised in the parent before `fork`.

## Items

### Item 1: the PTY tests from R1 item 1, plus the CI matrix that exposes the bug

R1's implementer wrote them (uncommitted) in `/home/stevecrawford/RiderProjects/redactingpty-wt/r1`:
`RedactingPty.TestHost/`, `RedactingPty.Tests/PtyRunnerTests.cs`, the test project's reference to
the host, the `RedactingPty.slnx` entry, and the library csproj changes (package validation against
the 1.0.0 baseline, `InternalsVisibleTo RedactingPty.TestHost`, `<Version>1.0.1-local</Version>`
because `0.0.0-local` failed pack validation with CP0003). Copy them in with the tests and their
assertions unchanged (only the host-path plumbing below changes); the design and the test list are
R1's item 1 (`docs/briefs/r1-dotnet10-idioms.md` on branch
`brief/r1`; read that item, its rules apply here too). Note: a NativeAOT-published host was left in
that tree's `bin/` from an experiment; build from clean.

Add to that:
- **A way to run the same tests against a NativeAOT-published host**: an environment variable
  (e.g. `REDACTINGPTY_TESTHOST`) naming the host binary; unset means the normally built JIT host.
- **CI matrix** (`ci.yml`), on ubuntu-latest, macos-latest and windows-latest: the tests with the
  JIT host built **Debug** (the default), with the JIT host built **Release**, and with the
  **NativeAOT** host (`dotnet publish RedactingPty.TestHost -c Release -r <rid> -p:PublishAot=true`
  with `<rid>` = `linux-x64`, `osx-arm64` (macos-latest is arm64), `win-x64`; then the tests with
  the variable set, scoped to that step, naming the per-OS binary (`.exe` on Windows)). W^X stays at
  its default (on) everywhere; no test or CI step may set `DOTNET_EnableWriteXorExecute`.
- Commit this item even though the Debug and Release JIT legs fail on Linux on unchanged code:
  that is the reproduction (the controller expects those red legs until item 3). Record the local
  Linux results for all three modes in the report (the local AOT publish works here: clang and the
  ILCompiler package are present).

### Item 2: root cause

Confirm or refute the hypothesis above with evidence you can gather here without downloading
anything (gdb is installed; JIT/stub logging via `DOTNET_` environment variables; a minimal
standalone repro if it helps). Answer in the report: what exactly runs in the child today (for
each of `execvp_native`, `write_ptr`, `_exit`, and `ExecFailedMsgPin.AddrOfPinnedObject()`), on a
Debug JIT host, a Release JIT host and NativeAOT, covering class initialisation, IL-stub creation,
first-call import resolution and precode patching, not only the calls themselves; why Release
passes the normal path but not the missing-command path; and why `Marshal.Prelink` before fork did
not help (which lazy stages it covers and which it leaves). If the hypothesis is wrong, say what is
right before item 3.

### Item 3: the fix

Acceptance, all three required:
1. **By construction:** an enumeration in the report of every instruction path the child executes
   between `fork` returning 0 and `execvp`/`_exit`, each shown to be compiled, resolved and
   initialised before `fork` (no stub, no lazy import, no class init, no allocation, no managed call
   other than code already compiled into `Run` itself). Green tests alone don't prove this: the
   Release leg passes today while compiling code in the child.
2. **By observation:** at least one experiment on the Debug JIT host showing no code generation in
   the child between fork and exec, before (it happens) and after (it doesn't): e.g. gdb with
   `set follow-fork-mode child` and a breakpoint on the JIT/stub entry points, or JIT/stub logging
   that tells the child's events apart. Commands and output in the report.
3. **By test:** the item-1 tests pass in all three CI modes on all three OSes with W^X on.

Design is yours, guided by item 2; state why it is sufficient on each host, macOS on its own terms. Candidates to evaluate
(say which you chose and why the others are worse):
- resolve `execvp`, `write` and `_exit` with `NativeLibrary.GetExport` in the parent and call them
  through blittable `delegate* unmanaged` function pointers, **inline in `Run`** (a helper method
  called only in the child would itself be JIT-compiled in the child in Debug, where nothing is
  inlined). Verify that library resolution matches the current imports on both OSes (`libc` on
  Linux as `LinuxPty`'s probe does; on macOS `libc` must resolve to libSystem's exports: check it).
  Check with logs whether the JIT needs any runtime-generated stub for such a call in Debug
  (minopts); for blittable signatures it should not. `delegate* unmanaged[SuppressGCTransition]`
  is optional: if you use it, show it compiles, and use it only for the child-side calls (its
  rules forbid blocking calls, which the parent side has);
- run `UnixPty`'s type initializer in the parent before `fork` and keep everything the child reads
  (the resolved pointers, the message address and length) in locals or already-initialised statics;
  compute `ExecFailedMsgPin.AddrOfPinnedObject()` and the length before `fork`;
- `posix_spawn` instead of `forkpty` (controlling-terminal setup differs between Linux and macOS;
  only if the others fail).
The parent side of `UnixPty` and `WindowsPty` do not change. Exit codes, the 127 diagnostic text,
timeouts and everything else stay as they are. Correct `UnixPty`'s header comment, which claims
"no managed allocation or CLR re-entry on either path" (false today), to describe the new
mechanism truthfully.
Break-it-once: revert the fix only; the Debug JIT tests fail on Linux again.

### Item 4: tswap's workaround (report only)

List where tswap sets `DOTNET_EnableWriteXorExecute=0` (its E2E tests,
`/home/stevecrawford/RiderProjects/tswap-wt/t15/TswapTests/`) and whether each is only about this
bug. Don't change tswap; the controller files that follow-up once 1.0.1 is out.

## Report only (no change in R0)

- `execvp` is not on POSIX's async-signal-safe list (`execve` is; `execvp`'s PATH search can
  allocate in a child of a multithreaded process), and the comment "full path bypasses PATH lookup"
  is wrong (`execvp` searches `PATH`). Resolving the path in the parent and calling `execve`/`execv`
  would fix both but changes behaviour: propose it for a later brief.
- With shared code pages, the parent's background tier-up or precode patching could in theory write
  pages the child executes during the fork-to-exec window; say whether you think that matters.

## Out of scope

R1's refactor (LibraryImport, `nint`, idioms) and R2. The child-path calls in R1 will keep R0's
mechanism.

## Report

`/home/stevecrawford/RiderProjects/redactingpty-wt/r0-report.md` (state in `r0-state.md`): item 2's
findings with evidence, the fix's design and why it suffices per host, local results for all three
modes before and after, and the final test summary lines.

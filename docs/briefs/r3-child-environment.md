# Brief R3: an environment for the child, never in its command line (1.2.0)

RedactingPty (github stevedcc/RedactingPty, NuGet `RedactingPty`, MIT) runs a child process in a
pseudo-terminal and redacts secrets from its output as it streams. tswap's `run` uses it. It targets
`net10.0`, is `IsAotCompatible`, and has no dependencies. Base: `origin/main` at 1e4ecb4 (1.1.0).

Rules: test first (watch each test fail for the right reason), one commit per task, `dotnet build
RedactingPty.slnx` warning-free and `dotnet test RedactingPty.slnx` green after each commit. No new
NuGet packages in the library. Never download or install an SDK or a toolchain (`export
DOTNET_ROOT=$HOME/dotnet PATH=$HOME/dotnet:$PATH`). Nothing needs `sudo`. Always build from clean when
switching between a JIT and a NativeAOT test host. Commits end with
`Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`, explicit paths, no push, merge or rebase.
A break-it-once check per key test, named in the commit message. Never change an existing test's
assertion; this brief changes none (it adds tests and test-host options). Stop and report when the
brief can't be built as written.

**Review history.** Round 1 (MiMo, GLM-5.3): both "Ready to implement: no". Folded in: the child's
environment is installed by the `environ` store only (the `execvpe` option is gone, and so is the
child-`PATH` test's skip clause); the "parent's environment unchanged" check moves into the test host,
where the runner runs; a new test that inherited variables not named survive; the inherited set is
read from the native `environ`, not the managed view; the Windows block's sort, terminator and flag are
pinned and its builder is unit-tested here; null names and values are rejected, and so are names that
differ only in case on Windows; test sets per runner (Windows uses `cmd` children, the `PATH` test is
Unix-only); the secret stays out of the test host's own argv; the default interface method throws only
for a non-empty `Environment`, with a test; "nothing changes in the child" is restated as what may and
may not run there, with R0's by-construction enumeration; the doc and csproj-comment drift is listed.
Rejected, with the reason beside the decision below: both reviewers' claims about where `execvpe`
reads `PATH` from.

Round 2 (MiMo, GLM-5.3): both "no", for one reason. Round 1's fix rejected the libc-scope lookup on a
reversed model, so it is corrected: `execvp` and `getenv` read `__environ`, and a copy relocation copies
only the symbol it names, so an `environ` copy in the executable is a dead second slot, not the live
one. Linux now resolves `__environ` (falling back to `environ`) and `environ` too, and stores to both
when they differ. Also: the Fallback tests move to Task 2 with the code they test (Task 1's commit
would have been red), with a Windows Fallback test added; the `PATH` test runs its script as argv
directly; `--env-file` rejects a line with no `=`; the `ToString` doc line is exact.

## Why

tswap #71 (owner: "It's important"): `tswap run --env VAR={{name}} -- cmd` is to hand a secret to the
child in its **environment**, so it is not in the child's argv, which on Linux every local user can
read in `/proc/<pid>/cmdline` while it runs (`/proc/<pid>/environ` is readable only by the same user
and root). Today `IPtyRunner.Run(argv, replacements, options)` has no way to set the child's
environment: the child inherits the parent's. tswap's own environment must not be changed to do it
(it is the caller's process; other threads read it; it would leak into later children).

## Task 1: the option, and the Unix runners

`RedactionOptions` gains:

```csharp
/// Variables set for the child only, on top of the inherited environment (a name given here
/// replaces the inherited value). Null or empty: the child inherits the environment unchanged.
public IReadOnlyDictionary<string, string>? Environment { get; init; }
```

The doc comment also says: the caller's process environment is never changed; the parent's native
copies are zeroed and freed after the spawn, best effort (the caller's managed strings can't be
zeroed); record equality compares the dictionary by reference, and `ToString` shows the
dictionary's own `ToString()` (a BCL dictionary's is its type name, never its contents). Qualify
`System.Environment` inside `RedactionOptions` where the property now shadows it.

**Validation, in one place:** an internal static validator called from `MatcherCarrier.For`, which
every built-in runner's options overload goes through before anything is spawned. `ArgumentException`,
nothing started, when a name is null or empty or contains `=` or NUL, or a value is null or contains
NUL (an empty value is allowed: `NAME=`). Names compare ordinally on Linux and macOS (`Path` and
`PATH` are two variables); on Windows, whichever runner, two given names equal under
`OrdinalIgnoreCase` are an `ArgumentException`, not a silent pick of one.

**UnixPty (Linux, macOS), under the fork-safety rules in its class comment and the README's "Fork
safety" section:** between `forkpty()` returning 0 and `execvp()`/`_exit()` the child runs no managed
code, no allocation, no P/Invoke stub, no static access, no lazy binding. So the options are read from
the `MatcherCarrier` **before** the fork (today they are read only after it, for the releaser; that
read stays), and the child's environment is built before `forkpty()`, in native memory, as a
NULL-terminated `char**`: the parent's inherited entries copied byte for byte from the native `environ`
(minus those whose name, the bytes before the first `=`, is given), plus the given ones, UTF-8. Read
the native `environ`, not `System.Environment.GetEnvironmentVariables()`: on Unix the runtime's
`SetEnvironmentVariable` changes only its managed copy, so the managed view is not what the child
inherits today, and it round-trips invalid UTF-8 lossily. (Reading `environ` while another thread calls
native `setenv` is the usual POSIX hazard, the one `execvp` already takes today.)

**The mechanism: assign `environ` in the child, nothing else.** Resolve the address (or addresses,
below) of the C environment pointer in the type initialiser (beside `ExecvpAddress`), copy them into
`Run` locals before the fork, and in the child branch, before `childExecvp`, do plain stores, inline in
`Run`: `if (nativeEnvp != 0) { *(nint*)environAddress = nativeEnvp; if (environAlias != 0)
*(nint*)environAlias = nativeEnvp; }` (never `Marshal.WriteIntPtr` or a helper: those are managed
calls). `execvp` itself is unchanged: its `PATH` search, the `ENOEXEC` `/bin/sh` fallback and `EACCES`
handling stay exactly as they are, and it searches the **child's** `PATH`, as `env VAR=x cmd` does
(glibc's `execvp` is `__execvpe (file, argv, __environ)`, musl's the same, and both take `PATH` from
`getenv`, which reads `__environ`; macOS's reads its `environ`). The stores land in the child's
copy-on-write pages, so the parent's environment is never written, by construction. Resolving the
addresses:

- **Linux:** through the global scope (`NativeLibrary.GetMainProgramHandle()`, `RTLD_DEFAULT`), resolve
  `__environ`, the name `execvp` and `getenv` read, falling back to `environ` if `__environ` isn't
  exported; that is `environAddress`. Also resolve `environ`; if it is a different address,
  `environAlias` is that address (else 0), and the child stores the block to both. In libc the names are
  one object (this machine's glibc: `__environ` GLOBAL, `environ` and `_environ` WEAK, all at
  `0x1f6de8`), but a copy relocation is per symbol name: a host executable with `R_X86_64_COPY` for
  `environ` gets a second slot under that name while libc keeps reading `__environ`, so a store to
  `environ` alone would write a dead copy and the child would silently inherit. The second store keeps
  the executable's own `environ` readers consistent too. Show in the commit message, for the JIT
  apphost and the NativeAOT host: `readelf -rW <host> | grep environ`, what each lookup returned, and
  whether the two addresses differed (the net10.0 apphost has `R_X86_64_GLOB_DAT` for `environ`, no
  copy, checked with `readelf` on a build here). Neither name found: throw in the type initialiser (a
  clear failure, never a child that silently inherits).
- **macOS:** resolve `_NSGetEnviron` from `Libc` and call it in the type initialiser (the parent); it
  returns the `char***` (dotnet/runtime's `SystemNative_GetEnviron` makes the same split).

**Why not `execvpe`** (both reviewers agreed; the reasons differ from theirs). `man 3 execvpe` (glibc):
"execvpe() searches for the program using the value of PATH from the caller's environment, not from the
envp argument". So it would search the parent's `PATH`, unlike `env VAR=x cmd`, on glibc as on musl
(MiMo cited musl's `__execvpe`, which uses `getenv("PATH")`); MiMo's claim that glibc's reads `envp`,
and GLM's that musl has none, are wrong or moot. macOS has none at all, so it would need the `environ`
store underneath anyway. Don't add it.

**With no `Environment` (null or empty)**, the child path gains only a compare of a local against 0 and
its branch: no call, allocation, stub, static or type-initialiser access, lazy binding or store, and no
native environment is built. The existing fork-safety tests stay green unchanged, on the JIT and the
NativeAOT hosts. As in R0, green tests are not the proof: the commit message enumerates every
instruction path the child executes between `forkpty` returning 0 and `execvp`/`_exit`, with and
without an environment, and shows each is a read of a `Run` local, the compares, the one or two
stores, or one of the three existing calls through the pointers.

After the spawn (whether the fork succeeded or failed), the parent zeroes the given values' bytes and
frees the whole native block (copy-on-write keeps the child's copy intact, as for argv today).

**Test host** (`RedactingPty.TestHost`), new options:
- `--env-file FILE`: the `Environment` dictionary from a file the test writes (one `NAME=VALUE` per
  line, split at the first `=`; a non-empty line with no `=` is a usage error, exit 121); an empty
  file is an empty dictionary, distinct from no option. The values never go in the host's argv, so
  the `/proc` test isn't undercut by the host's own cmdline.
- `--report-own NAME`: after `Run` returns, the host writes `own-NAME=<value>` to stderr from
  `System.Environment.GetEnvironmentVariable` and, on Unix, `own-native-NAME=<value>` from libc
  `getenv` (empty when unset).

Either one runs the options overload; with only these options `Matchers` is empty (existing option
combinations are unchanged). `RunHost` and `HostSession.Start` gain an optional seed for the host's own
environment (`psi.Environment`), for inherited variables. Variable names in tests carry a GUID, so
nothing is inherited from CI by accident. Children: `sh -c` on Unix, as the existing tests, except
the `PATH` test below; no new `ChildModes` mode is needed.

Tests, `[UnixPtyFact]` (both forkpty runners), through the host, unless marked:
- `Environment_SetsVariableForTheChild` (`sh -c 'echo "got-$NAME"'`; and `--report-own NAME`: the
  host's own view, managed and native, is empty after `Run`);
- `Environment_OverridesAnInheritedVariable` (seeded in the host as `old`, given as `new`: the child
  prints `new`, the host still reports `old`);
- `Environment_InheritsUnoverriddenVariables` (a seeded variable not in `Environment` reaches the child
  beside a given one);
- `Environment_NullOrEmpty_InheritsUnchanged` (no option, and an empty file: the seeded variable
  reaches the child unchanged);
- `Environment_ValueNotInTheChildsCommandLine` (`[LinuxPtyFact]`; child `sh -c 'echo "pid=$$"; read x'`:
  wait for the `pid=` line with `WaitForOutput`, then assert the value's bytes are absent from
  `/proc/<pid>/cmdline` and `NAME=value` is present in `/proc/<pid>/environ`; then `Send` a line and
  finish the session; no sleep);
- `Environment_ValueStillRedactedInOutput` (the value given as a `StreamReplacement` too: the child
  prints it, the raw output never holds it, the text shows the replacement);
- `Environment_ChildPathIsSearched` (a temp folder holding an executable `#!/bin/sh` script with a GUID
  name, `PATH` given in `Environment` as that folder only, and the child argv is just that name (no
  slash, no `sh -c`: `sh` isn't on that `PATH`, and a shell would move the search out of `execvp`; the
  kernel runs the shebang): the script runs and prints its marker, exit 0; never skipped on Unix);
- in process, `[Fact]`: each built-in runner on this OS, and `FallbackPty`, given each invalid name or
  value above (null through a custom dictionary): `ArgumentException`, and the argv (a command that
  creates a marker file: `sh -c` on Unix, `cmd /c` on Windows) never ran: the marker is absent.

Both test hosts (JIT and NativeAOT) run the new tests, as the existing PTY tests do.

Break-it-once: (1) drop the store in the child; `Environment_SetsVariableForTheChild` fails. (2) Put the
value into argv instead; `Environment_ValueNotInTheChildsCommandLine` fails. (3) Build the block from
the given variables only; `Environment_InheritsUnoverriddenVariables` fails. (4) Call `execvpe` with the
block instead of the store (glibc); `Environment_ChildPathIsSearched` fails. (5) Call `setenv` in the
runner instead; `Environment_SetsVariableForTheChild`'s `own-native-` check fails. (6) On a host where
the two Linux addresses differ, store to `environ` only; `Environment_SetsVariableForTheChild` fails
there (if neither host has a copy, say so and that this check could not be run).

## Task 2: Windows and the fallback runner

- **WindowsPty:** pass an environment block to `CreateProcessW` (`lpEnvironment`, today 0) and OR
  `CREATE_UNICODE_ENVIRONMENT` (`0x00000400`) into `dwCreationFlags` beside
  `EXTENDED_STARTUPINFO_PRESENT`. The block: the inherited variables
  (`System.Environment.GetEnvironmentVariables()`, which on Windows reads the live process block) with
  the given ones applied, names compared `OrdinalIgnoreCase`; sorted by name with
  `StringComparer.OrdinalIgnoreCase` (the documented rule, "case-insensitive, Unicode order, without
  regard to locale"; GLM noted that Windows tolerates an unsorted block: sort anyway, the docs ask for
  it); UTF-16, each entry `name=value\0`, then one more `\0` (two zero code units at the end). The
  hidden `=C:`-style drive entries are not in the managed view and are dropped (cmd recreates them, and
  `Process.Start` drops them too); say so in a comment. Zero and free the block after the call. With
  no `Environment`, pass 0 and today's flags.
- Build the block in an internal pure function (dictionary in, `char[]` out) and unit-test it here,
  cross-platform (`[Fact]`): mixed-case sort order, a case-insensitive override (a given `PATH`
  replaces an inherited `Path`), the separators and the double terminator. Only the wiring is left to
  CI.
- **FallbackPty:** `ProcessStartInfo.Environment[name] = value` for each (it starts from what
  `Process.Start` passes today, so inheritance there is unchanged). Tests, here and not in Task 1 so
  that Task 1's commit stays green: `Fallback_Environment_SetsVariableForTheChild` and
  `Fallback_Environment_OverridesAnInheritedVariable` (`[UnixPtyFact]`, `runner: "fallback"`, as the
  existing `Fallback_*` tests), and `Fallback_Environment_SetsVariableForTheChild_Windows`
  (`[WindowsPtyFact]`, `runner: "fallback"`, a `cmd /c` child).
- Tests on Windows (`[WindowsPtyFact]`, `cmd /c echo got-%NAME%` children, the pattern the existing
  Windows tests use): `SetsVariable`, `Overrides`, `InheritsUnoverridden`, `NullOrEmpty`,
  `StillRedacted`. Not the `/proc` test, and not `ChildPathIsSearched`: `CreateProcessW` and
  `Process.Start` find the program through the parent's `PATH`, so a `PATH` given in `Environment`
  reaches the child only as a variable. You can't run Windows here: CI runs them; say so in the commit
  message.

Break-it-once: (1) sort the block with `StringComparer.Ordinal`; the block builder's sort test fails.
(2) Drop the `FallbackPty` loop; `Fallback_Environment_SetsVariableForTheChild` fails. (3) Drop
`CREATE_UNICODE_ENVIRONMENT`: it can't run here, so show by reasoning in the commit message that the
UTF-16 block would be read as ANSI (garbled names, no `SystemRoot`), and rely on CI.

## Task 3: docs, the contract and the version

- `IPtyRunner.Run(…, RedactionOptions)`: the default interface implementation delegates only when
  `Matchers` is empty, `ReleaseWhenAwaitingInput` is off and `Environment` is null or empty, and
  otherwise throws `NotSupportedException` (never silently do less). Its XML doc says so, and that a
  runner which overrides this overload enforces the rule itself. Tests beside the existing
  `CustomRunner_*` ones (which stay unchanged): `CustomRunner_DefaultOverload_WithEnvironmentThrows` (a
  non-empty `Environment`: `NotSupportedException`, the 2-arg `Run` never called) and
  `CustomRunner_DefaultOverload_EmptyEnvironmentDelegates`. Break-it-once: leave the condition as it
  is; the first fails.
- README: a "Setting the child's environment (1.2)" section under Usage: what `Environment` does, that
  the values never reach the child's argv, who can still read them (on Unix the same user and root,
  `/proc/<pid>/environ`; on Windows anyone who can open the child process), that redaction still
  applies to the output, that a `PATH` given there is searched for the program on Linux and macOS but
  not on Windows, and that `FallbackPty` applies it through `ProcessStartInfo.Environment`. The Fork
  safety section: the child branch is now the three calls plus, with an environment, one pointer store
  (two where `__environ` and `environ` differ), whose addresses are resolved before the fork. The
  matchers paragraph's `NotSupportedException` sentence names `Environment` too.
- Code comments: `UnixPty`'s class comment (what the child branch runs, now with the stores) and
  `MatcherCarrier`'s "signature and code are frozen" (the signature still is; `Run`'s body now also
  builds the environment before the fork).
- `RedactingPty.csproj`: `PackageValidationBaselineVersion` to 1.1.0 (the API only grows), the local
  `<Version>` placeholder to `1.2.0-local`, and both comments that name the baseline say 1.1.0.

## Out of scope

Removing inherited variables, a working directory option, tswap's `--env` (tswap brief, after this
release), the R2 .NET 11 test targets, musl testing (still untested, as the README says).

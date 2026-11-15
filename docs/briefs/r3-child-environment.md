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
assertion. Stop and report when the brief can't be built as written.

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

Rules, enforced in one place before anything is spawned (`ArgumentException`, nothing started): a name
is non-empty and contains no `=` and no NUL; a value contains no NUL. Names compare ordinally on Linux
and macOS.

**UnixPty (Linux, macOS), under the fork-safety rules in its class comment and the README's "Fork
safety" section:** between `forkpty()` returning 0 and `execvp()`/`_exit()` the child runs no managed
code, no allocation, no P/Invoke stub. So the child's environment is built **before** `forkpty()`, in
native memory, as a NULL-terminated `char**` (the inherited variables, minus the overridden names, plus
the given ones, UTF-8), and the child branch only installs it and execs. Two ways that keep today's
`execvp` semantics (PATH search, the `ENOEXEC` fallback, `EACCES` handling) exactly; pick one, justify
it in the commit message, and keep the other out:

- **Assign `environ` in the child:** resolve the address of the C `environ` variable before the fork
  (Linux glibc and musl export `environ`; on macOS call `_NSGetEnviron()` before the fork, which
  returns `char***`), then in the child a single pointer store `*environPtr = nativeEnvp` (a plain
  memory write, no call) before `execvp`. `execvp` then searches the **child's** `PATH`, as
  `env VAR=x cmd` does.
- **`execvpe`** where it exists (glibc), with a fallback for macOS, which has no `execvpe`.

With no `Environment` (null or empty), nothing in the child path changes: no new code runs between the
fork and the exec. Show it: the existing fork-safety tests stay green unchanged, and the JIT and
NativeAOT test hosts both pass.

After the spawn, the parent zeroes and frees the native environment block (best effort: the managed
strings can't be zeroed; say so in the doc comment).

Tests (`PtyRunnerTests`, through `RedactingPty.TestHost`; add a child mode that prints one named
variable if there isn't one):
- `Environment_SetsVariableForTheChild` (the child prints it; the parent's own environment is unchanged
  afterwards: `System.Environment.GetEnvironmentVariable` returns what it returned before);
- `Environment_OverridesAnInheritedVariable`;
- `Environment_NullOrEmpty_InheritsUnchanged`;
- `Environment_ValueNotInTheChildsCommandLine` (Linux: while the child waits, read `/proc/<child>/cmdline`
  and assert the value is absent, and `/proc/<child>/environ` holds it; wait on a concrete signal from the
  child, never a sleep);
- `Environment_ValueStillRedactedInOutput` (the value given as a `StreamReplacement` too: the child prints
  it, the output shows the replacement);
- `Environment_ChildPathIsSearched` (the program is found through a `PATH` given in `Environment` only, if
  you chose the `environ` way; skip this test with its reason if you chose a way that searches the parent's
  `PATH`, and say so in the README);
- invalid names and values: `ArgumentException`, no child started.

Both test hosts (JIT and NativeAOT) run the new tests, as the existing PTY tests do.

Break-it-once: (1) drop the environment install in the child; `Environment_SetsVariableForTheChild` fails.
(2) Put the value into argv instead; `Environment_ValueNotInTheChildsCommandLine` fails.

## Task 2: Windows and the fallback runner

- **WindowsPty:** pass an environment block to `CreateProcessW` (`lpEnvironment`, today 0) with
  `CREATE_UNICODE_ENVIRONMENT`: the inherited variables with the given ones applied, names compared
  case-insensitively (Windows rules), sorted as Windows expects, double-NUL terminated. Zero and free it
  after the call. With no `Environment`, pass 0 as today.
- **FallbackPty:** `ProcessStartInfo.Environment[name] = value` for each.
- Tests: the Task 1 set that applies there (not the `/proc` one), behind the platform guards the existing
  Windows tests use. You can't run Windows here: CI runs them; say so in the commit message.

Break-it-once: drop `CREATE_UNICODE_ENVIRONMENT`; show the flag is needed by reasoning in the commit
message (it can't run here), and rely on CI.

## Task 3: docs and the version

- README: a "Setting the child's environment (1.2)" section under Usage: what `Environment` does, that the
  values never reach the child's argv, who can still read them (the same user, root: `/proc/<pid>/environ`),
  and that redaction still applies to the output; the Fork safety section gains a sentence on how the
  environment is installed in the child.
- `RedactingPty.csproj`: `PackageValidationBaselineVersion` to 1.1.0 (the API only grows) and the local
  `<Version>` placeholder to `1.2.0-local`; the comment beside it still holds.
- The XML doc of `IPtyRunner.Run(…, RedactionOptions)`: a runner that doesn't support `Environment` must
  throw `NotSupportedException` when one is given (the same rule as the matchers: never silently do less);
  the default interface implementation enforces it.

## Out of scope

Removing inherited variables, a working directory option, tswap's `--env` (tswap brief, after this
release), the R2 .NET 11 test targets.

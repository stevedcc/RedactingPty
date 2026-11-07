# RedactingPty

[![NuGet](https://img.shields.io/nuget/v/RedactingPty.svg)](https://www.nuget.org/packages/RedactingPty)

Run a child process inside a pseudo-terminal (PTY) while intercepting everything it
writes, applying find/replace filters to the stream before it reaches the terminal.
The child sees a real TTY — colour output, progress bars, and interactive prompts all
work — but its output never reaches the screen unfiltered.

Built for secret redaction (mask credential values in subprocess output as they stream
by), but the API is a generic replacement filter: the caller supplies
`StreamReplacement(Find, Replace)` pairs and decides what replacements look like.

This library is self-contained — no dependencies beyond the .NET BCL — and is
AOT-compatible (`IsAotCompatible`; safe to consume from a NativeAOT-published binary).

## Install

```
dotnet add package RedactingPty
```

## Usage

```csharp
using RedactingPty;

var replacements = new List<StreamReplacement>
{
    new("hunter2", "[REDACTED: db-password]"),
};

// argv[0] is executed directly (no shell). Output streams through the filter.
int exitCode = PtyRunnerFactory.Create().Run(["kubectl", "logs", "my-pod"], replacements);
```

`PtyRunnerFactory.Create()` picks the right implementation for the current platform
and console state; `Run` blocks until the child exits and returns its exit code.
Caller stdin is forwarded to the child so interactive programs (ssh, `kubectl exec`)
work.

### Custom matchers (1.1)

When the caller cannot hand over a find-string as plaintext (for example a value it knows only
by a keyed fingerprint), it supplies an `IStreamMatcher` through `RedactionOptions`:

```csharp
var options = new RedactionOptions { Matchers = [myMatcher], ReleaseWhenAwaitingInput = true };
int exitCode = PtyRunnerFactory.Create().Run(argv, replacements, options);
```

The contract:

- `MaxMatchLength` (read once, at least 1) bounds every match `FindMatches` reports.
- `FindMatches(window, matches)` adds every match lying entirely inside the window, in window
  coordinates; overlapping matches are fine. A match outside the window, empty, longer than
  `MaxMatchLength` or with a null label throws `InvalidOperationException` (whose message names
  indices, lengths and the matcher's type, never output text).
- `PendingFrom(window)` is the smallest index from which a match could still start and end past
  the window: the redactor holds back everything from there (the default holds the last
  `MaxMatchLength - 1` characters). It must lie in `[0, window.Length]`.
- **Thread safety:** a matcher must be stateless per call and safe to call from several threads
  at once: the pipe runner (`FallbackPty`) drains stdout and stderr on two threads with one matcher.

Masking with matchers: no character of a complete occurrence, plaintext or matcher, is ever
emitted. Overlapping occurrences (sharing a character) merge into one run, written once with the
label of its longest occurrence, then the leftmost, then plaintext before matcher, then list
order; adjacent occurrences keep their own labels, and replacement text is never matched again.
Without a matcher (the 1.0 overloads, or an empty list) the 1.0 algorithm runs unchanged.

**Hold-back.** The plaintext part holds back only a real prefix of a `Find`; each matcher holds
what its `PendingFrom` says. Whatever is held appears late, so a matcher's `PendingFrom` is
observable by the child through timing: base it on nothing secret.

**Failure is closed.** If a matcher (or the redactor) throws while output streams, the runner
discards the held text and that chunk, writes `[output truncated: redaction failed]`, keeps
reading and discarding the child's output (so the child never blocks on a full PTY or pipe), and
rethrows the exception from `Run` once the child has exited. Nothing after the failure point is
written. A runner that does not implement the `RedactionOptions` overload (the interface's
default) throws `NotSupportedException` for any matcher rather than redact less.

### Releasing held output at a prompt (1.1)

A matcher that holds a lot (say, any text that could begin a multi-line secret) would hide an
interactive prompt behind the held text. With `ReleaseWhenAwaitingInput`, once held output has
been quiet for 50 ms the runner checks whether the child waits for terminal input, and again
every 100 ms while output stays quiet. **Only a positive check releases; time alone never does.**
Released text is resolved like a flush: complete occurrences are masked first.

| Platform | Released when |
|---|---|
| Linux | no thread of the PTY's foreground process group is running and one is blocked in `read` on the terminal (an fd on the slave, or `/dev/tty` with the slave as controlling terminal), from `/proc`. Anything unreadable: held. Programs that wait in `poll`/`select` (bash's readline) stay held. Detected: `read` builtins, Python's `input()` and `getpass`, OpenSSH passphrase prompts, `gpg` loopback prompts. |
| macOS | the terminal's ECHO flag is off and ICANON is on (password and passphrase prompts). A plain `y/N` prompt (echo on) stays held until more output or exit; raw-mode programs (vim, less) clear both and are not released. |
| Windows (ConPTY), pipes (`FallbackPty`) | never: no signal. Held text waits for more output or exit. |

**Residual limit.** Only *complete* occurrences are recognised; prefixes deliberately are not.
Text that lies in no complete occurrence is emitted when it leaves the hold window, at a release
or at exit, so a secret printed only in part (cut short, split by an input wait, completed later
by another thread or a background job, or split between stdout and stderr under `FallbackPty`,
which redacts each stream alone) shows that part. The 1.0 path behaves the same.

## Platform support

| Platform | Implementation | Mechanism |
|---|---|---|
| Linux | `LinuxPty` | `forkpty(3)` from glibc ≥ 2.34, falling back to `libutil.so.1` (probed at startup) |
| macOS | `MacOSPty` | `forkpty(3)` from `libutil.dylib` |
| Windows 10 1809+ | `WindowsPty` | ConPTY (`CreatePseudoConsole`) |
| Anything else, or any redirected stdio | `FallbackPty` | plain pipes via `System.Diagnostics.Process` (no TTY semantics) |

The factory falls back to `FallbackPty` whenever stdin, stdout, or stderr is
redirected: PTY master bytes would corrupt a downstream pipe consumer, a PTY cannot
half-close its input side (the child would hang waiting for EOF), and a PTY merges
stderr into stdout, which would silently ignore a stderr redirect.

## Design notes

### Streaming redaction (`StreamRedactor`)

Output arrives in arbitrary read-buffer chunks, so a find-string can straddle a chunk
boundary. `StreamRedactor` keeps a sliding-window tail of `longestFind - 1` characters
between chunks — the minimum overlap that guarantees any single find-string, split at
any position, is seen in full. Additional care:

- If a match straddles the emit/tail boundary, the emit point is pulled back to the
  start of the match (repeated until stable) so neither half is emitted unfiltered.
- UTF-16 surrogate pairs are never split at the emit boundary.
- Replacements are applied longest-Find-first (the constructor re-sorts defensively)
  so a shorter find-string sharing a prefix never clobbers a longer one.

### Fork safety (`UnixPty`)

The forked child must run no code generated after `fork()`. With W^X on (the .NET
default), Linux maps JIT code as shared memory, so a child that compiles a stub, binds a
P/Invoke lazily or runs a type initializer races the still-running parent and can crash
(1.0.0 did, with exit 139, on Linux JIT hosts; NativeAOT hosts, macOS and Windows were
unaffected). So:

- `execvp`, `write` and `_exit` are resolved with `NativeLibrary` in the parent and called
  from the child through unmanaged function pointers held in locals; the type initializer
  is forced before the fork; argv and the exec-failure message are in native or pinned
  memory computed before the fork. The child branch is three direct calls into libc.
- The library is always compiled optimised and `Run` is `AggressiveOptimization`:
  unoptimised (debug) JIT code routes even function-pointer calls through a stub built on
  first call, which would be in the child.

**Known limitations:** a debugger that suppresses JIT optimisation for the library, or a
profiler that disables inlined P/Invoke transitions (even with optimisation on), brings
that stub back, so the child can crash under them on Linux. musl (Alpine) is untested. If a garbage collection is suspending threads at the
exact moment of the fork, the child can hang in the runtime's return path and the parent
waits for it indefinitely (rare; present since 1.0.0; tracked in the issues).

### Drain timeout and truncation

After the child exits, descendants may keep inherited slave-PTY fds open indefinitely.
The read loop gets 30 s to drain naturally (EIO once all slave fds close), then is
cancelled via a flag checked on a 200 ms poll cadence — never by closing the fd from
another thread, which is undefined behaviour on POSIX. On cancellation the redactor's
retained tail is **discarded** (it may contain a partial, unfiltered find-string) and
`[output truncated]` is emitted instead — deliberately losing output rather than
risking a leak.

## Testing

`RedactingPty.Tests` covers the chunk-boundary sliding-window logic, the fallback drain
plumbing, and the real PTY runners end to end: `RedactingPty.TestHost` runs a child through
`LinuxPty`, `MacOSPty` or `WindowsPty` directly, and the tests check redaction (including
secrets that straddle read boundaries), exit codes, signals, TTY detection and the
exec-failure diagnostic. CI runs them on Linux, macOS and Windows against a Debug JIT host,
a Release JIT host and a NativeAOT host (`REDACTINGPTY_TESTHOST` names the host binary).

## Origin

Extracted from [tswap](https://github.com/stevedcc/TokenSwap), a YubiKey-backed
secret manager, where it's used to redact secrets from subprocess output (e.g.
`tswap run kubectl logs my-pod`).

## License

MIT — see [LICENSE](LICENSE).

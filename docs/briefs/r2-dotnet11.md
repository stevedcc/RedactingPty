# Brief R2: RedactingPty tested on .NET 11 (RC 3 now, GA SDK on release day)

Same rules as R1 (one commit per item, build warning-free and tests green after each, no new
packages (restoring the .NET 11 `Microsoft.DotNet.ILCompiler` from nuget.org for the AOT check is
a package restore and allowed, as in R1), never download or install an SDK: the .NET 11 RC 3 SDK `11.0.100-rc.3.26522.148` is in
`~/dotnet`).

## Why

**Order:** after R1 is merged (it is, `b4fda29`); branch from `main` at that point. **Revised
2026-11-01 after R0/R1:** R0 (released as 1.0.2) added the real-PTY tests, the test host and a CI
matrix of three host modes (Debug JIT, Release JIT, NativeAOT) per OS, and froze the fork-child
spans (R1's brief lists them; README "Fork safety" says why). Those spans stay frozen here. Owner (2026-11-01): "First of all
idiomatic net 10, then net 11." Like tswap's T15: retarget on RC 3 now, get CI green on all three
OSes, keep the PR open until .NET 11 GA, then move `global.json` to the GA SDK, merge, and tag on
the owner's go. tswap's own .NET 11 PR (#214) consumes RedactingPty 1.0.2 (net10.0) on net11.0 and its suite
passes, but tswap's CI runs the NativeAOT binary; **the .NET 11 JIT legs here are the first real
check that R0's fork-child fix holds on the .NET 11 JIT** (the fix depends on how the JIT compiles
`Run`'s unmanaged function-pointer calls).

## Decisions

1. **SDK:** `global.json` → `"version": "11.0.100-rc.3.26522.148"`, `"rollForward":
   "latestFeature"` (unchanged).
2. **Target frameworks (owner, 2026-11-01):** the **library stays `net10.0` only**; the **test
   project and the test host (`RedactingPty.TestHost`) target `net10.0;net11.0`**, so CI runs every test on both runtimes
   on all three OSes. Reasoning: .NET 10 is the LTS and "lots of people will skip 11"; a
   `net10.0` build already runs on .NET 11 (tswap's .NET 11 PR uses RedactingPty 1.0.0 that way),
   and a NativeAOT consumer compiles the library with its own ILCompiler, so a `net11.0` library
   build adds nothing unless the code uses a .NET 11-only API. If that ever happens, adding the
   target is a separate, small change.
3. **Breaking changes:** walk
   `/home/stevecrawford/RiderProjects/croft/.claude/skills/migrate-dotnet10-to-dotnet11/SKILL.md`
   and its `references/` (`core-libraries-`, `runtime-jit-`, `sdk-msbuild-`,
   `csharp-compiler-dotnet10to11.md`; interop and process-related entries matter most here) as a
   checklist, plus Microsoft's ".NET 11 breaking changes" page for Preview 4 through RC 3 (read only;
   if you can't reach it, say so). The skill's references stay authoritative for what they cover;
   the page only adds the later previews and RCs. One report line per relevant entry: affects RedactingPty or not,
   and why.
4. **Idioms:** none. The library compiles as `net10.0` (C# 14); C# 15 forms are not available
   to it. Test code you write follows `writing-dotnet10-code` (it compiles for both targets).
5. **CI:** `ci.yml` and `publish.yml` both read the SDK from `global.json` (`global-json-file`); the
   .NET 11 SDK builds and runs both test targets, but the `net10.0` tests need the .NET 10
   runtime, which the .NET 11 SDK does not include: make CI's `setup-dotnet` install `10.0.x`
   alongside the SDK `global.json` names. **Matrix:** keep the three modes per OS. In the JIT
   modes the tests run for both target frameworks (each framework's test run launches the test
   host built for the same framework, so the child-side code really runs on that runtime: check
   how the test locates the host and make it framework-specific if it isn't). In the `aot` mode,
   publish the host for both frameworks and run the tests once against each (the
   `REDACTINGPTY_TESTHOST` variable per run). Every leg's log must show test totals for both
   `net10.0` and `net11.0`; the controller checks them per leg. ubuntu / jit-debug stays the canary
   leg (keep the ci.yml comment). W^X stays at its default everywhere. The packed `.nupkg` still
   has only `lib/net10.0`, and package validation against 1.0.0 still passes. `publish.yml` runs
   the tests before packing: it must run both frameworks too (install `10.0.x` there as well).
6. **NativeAOT:** `dotnet publish RedactingPty.TestHost -f net11.0 -c Release -r <rid> -p:PublishAot=true`
   reports 0 AOT/trim warnings, as `-f net10.0` does today.
7. **No library code change** is expected. If a .NET 11 breaking change or a failing .NET 11 leg
   needs one, stop and report it with the evidence before changing anything (and never inside the
   frozen spans without the owner's decision).

## Items

1. SDK and test targets (decisions 1, 2, 5); CI green on all three OSes, both runtimes.
2. Breaking-change walk (decision 3), report only unless an entry needs a code change (then its own
   commit with a test where one is possible).

## GA day (not this brief)

`global.json` → the GA SDK, CI green, external review, merge. No new package is needed for R2
alone (the library build is unchanged); R1's refactor (merged after 1.0.2) ships as its own patch
release whenever the owner tags it (proposal 1.0.3).

## Report

`~/RiderProjects/redactingpty-wt/r2-report.md`; state in `r2-state.md`.

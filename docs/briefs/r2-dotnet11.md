# Brief R2: RedactingPty tested on .NET 11 (RC 3 now, GA SDK on release day)

Same rules as R1 (one commit per item, build warning-free and tests green after each, no new
packages (restoring the .NET 11 `Microsoft.DotNet.ILCompiler` from nuget.org for the AOT check is
a package restore and allowed, as in R1), never download or install an SDK: the .NET 11 RC 3 SDK `11.0.100-rc.3.26522.148` is in
`~/dotnet`).

## Why

**Order:** after R1 is merged; branch from `main` at that point. Owner (2026-11-01): "First of all
idiomatic net 10, then net 11." Like tswap's T15: retarget on RC 3 now, get CI green on all three
OSes, keep the PR open until .NET 11 GA, then move `global.json` to the GA SDK, merge, and tag on
the owner's go. tswap's own .NET 11 PR (#214) already consumes RedactingPty 1.0.0 (net10.0) on
net11.0 without trouble, so nothing forces this; it keeps the library current.

## Decisions

1. **SDK:** `global.json` → `"version": "11.0.100-rc.3.26522.148"`, `"rollForward":
   "latestFeature"` (unchanged).
2. **Target frameworks (owner, 2026-11-01):** the **library stays `net10.0` only**; the **test
   project and the R1 test host target `net10.0;net11.0`**, so CI runs every test on both runtimes
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
5. **CI:** `ci.yml` and `publish.yml` both read the SDK from `global.json` (`global-json-file`); the .NET 11 SDK builds and
   runs both test targets, but the `net10.0` tests need the .NET 10 runtime, which the .NET 11 SDK
   does not include: make CI's `setup-dotnet` install `10.0.x` alongside the SDK `global.json`
   names, and check that the test log shows both `net10.0` and `net11.0` runs). The packed `.nupkg` still has only
   `lib/net10.0`, and R1's package validation against 1.0.0 still passes.
6. **NativeAOT:** R1's AOT check (a tiny NativeAOT app referencing the library) also publishes
   for `net11.0` (`-f net11.0`, the host being multi-targeted) with 0 AOT/trim warnings, and R1's PTY tests pass on .NET 11 on all three OSes.

## Items

1. SDK and test targets (decisions 1, 2, 5); CI green on all three OSes, both runtimes.
2. Breaking-change walk (decision 3), report only unless an entry needs a code change (then its own
   commit with a test where one is possible).

## GA day (not this brief)

`global.json` → the GA SDK, CI green, external review, merge. No new package is needed for R2
alone (the library build is unchanged); R1's refactor ships as its own patch release whenever the
owner tags it (proposal 1.0.1).

## Report

`~/RiderProjects/redactingpty-wt/r2-report.md`; state in `r2-state.md`.

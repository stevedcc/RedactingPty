# Brief R2: RedactingPty on .NET 11 (RC 3 now, GA SDK on release day)

Same rules as R1 (one commit per item, build warning-free and tests green after each, no new
packages, never download or install an SDK: the .NET 11 RC 3 SDK `11.0.100-rc.3.26522.148` is in
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
2. **Target framework: OWNER DECISION PENDING** (R2 does not start until it is settled):
   - **(a) Multi-target `net10.0;net11.0` (recommended).** .NET 10 is the LTS (supported to
     November 2028); a library that drops it forces every consumer onto .NET 11 (an STS). The
     package gains a `net11.0` build; `net10.0` consumers are unaffected. Cost: the code stays
     C# 14 (the `net10.0` build compiles with C# 14; C# 15 forms such as `closed` records, unions
     and `with(...)` collection arguments can't be used without `#if`), so the ".NET 11 idioms"
     part is limited to runtime-level differences, if any.
   - **(b) `net11.0` only.** C# 15 and every .NET 11 API become usable; every consumer must be on
     .NET 11.
   The tests then run on every target framework (`net10.0;net11.0` in the test project for (a)).
3. **Breaking changes:** walk
   `/home/stevecrawford/RiderProjects/croft/.claude/skills/migrate-dotnet10-to-dotnet11/SKILL.md`
   and its `references/` (`core-libraries-`, `runtime-jit-`, `sdk-msbuild-`,
   `csharp-compiler-dotnet10to11.md`; interop and process-related entries matter most here) as a
   checklist, plus Microsoft's ".NET 11 breaking changes" page for Preview 4 through RC 3 (read only;
   if you can't reach it, say so). One report line per relevant entry: affects RedactingPty or not,
   and why.
4. **Idioms:** with (a), no code modernisation beyond what a warning or a breaking change forces
   (follow `writing-dotnet11-code` for any line you write or change). With (b), apply the
   `writing-dotnet11-code` defaults table where it has real sites (R1's report lists the
   candidates); the R1 rules (behaviour unchanged, public API unchanged, fork safety) still hold.
5. **CI:** `ci.yml` and `publish.yml` already read the SDK from `global.json`; with (a) make sure
   both target frameworks are built, tested and packed (the pack sanity step shows both `lib/`
   folders in the `.nupkg`).
6. **NativeAOT:** R1's AOT check (a tiny NativeAOT app referencing the library) publishes with 0
   AOT/trim warnings for `net11.0`, and the item-1 PTY tests from R1 pass on .NET 11 on all three
   OSes.

## Items

1. Retarget (decisions 1, 2) and fix what breaks; CI green on all three OSes.
2. Breaking-change walk (decision 3), report only unless an entry needs a code change (then its own
   commit with a test where one is possible).
3. Idioms per decision 4.

## GA day (not this brief)

`global.json` → the GA SDK, CI green, external review, merge, tag on the owner's go (version: the
owner's call; proposal 1.1.0 for (a), since a new target framework is additive). Then tswap's
`RedactingPty` reference can move to it (Renovate, tswap #218, or by hand).

## Report

`~/RiderProjects/redactingpty-wt/r2-report.md`; state in `r2-state.md`.

# Resume context

Updated: 2026-10-26

Read this at the start of every session, then
[development-workflow.md](development-workflow.md), then
[poseidon-master-plan.md](poseidon-master-plan.md), then the feature file named
under "Last changes made". Rewrite the last two parts as the work moves; they
describe the present, not the history.

## Primary resume context

**What the project is.** Poseidon is a modular maritime desktop application
(.NET 10, Avalonia 11.3.7) that will read real S-101 chart data and support
civilian navigation, with a later military (CMS) side. The host executable is
`System/Core/Core`; every other runtime project is a module library loaded at
run time from a `modules` directory and hot-reloaded without restarting the
host. The first milestone (M1) is the initial core application: Core, CoreUI,
Display, Charts, Navigation, Tracks, AIS, Radar and Sonar. CMS is long-term
backlog and is not started. It is a development application, not certified
ECDIS.

**Where things are.**

| What | Where |
|---|---|
| Host executable and module runtime | `System/Core/Core` |
| Core modules (`Core.UI`, `Core.Display`, `Core.Charts`, `Core.Navigation`, `Core.Tracks`, `Core.AIS`, `Core.Radar`, `Core.Sonar`) | `System/Core/Core<Name>`, each with a `module.json` manifest |
| Shared contracts (host-supplied types) | `System/Shared/Contracts` |
| CMS modules (deferred) | `System/CMS` |
| Tests | `Tests/Core/<Module>.Tests`, `Tests/Shared`, `Tests/Fixtures`, aggregates in `Tests/Aggregate` |
| Reference data and dataset mappings | `Reference/Data` |
| Plans and this context | `Reference/Data/Plan-Memory` |
| Chart input data format mappings (S-101, S-158, S-164) | `Reference/Data/Plan-Memory/Feature-Mappings` |
| CI | `.github/workflows/build-test-package.yml` and `.github/scripts/ci-linux.sh` |

**Commands** (run from the repository root, Windows paths and PowerShell).

- Build host and modules: `dotnet build Core.Application.proj`
- All tests: `dotnet test Poseidon.slnx`
- One project: `dotnet test Tests\Core\CoreTracks.Tests`
- Targets Windows, Arch Linux and RHEL-compatible; macOS is out of scope. CI
  runs on pull requests and manual dispatch only.

**Conventions a new session needs.**

- Every feature follows the workflow: plan, then tests, then code, with a gate
  after each step. Stop and wait for the user's explicit order to continue at
  each gate. Do not start a later step early.
- The question tool does not reach the user. Ask questions in plain text in the
  conversation, record them in the feature file's open questions, and wait.
- Nothing is committed unless the user asks.
- File changes are made with the editor tools; shell commands are for running
  builds and tests.
- Module identity, version and contributions come only from each module's
  `module.json`. Civilian Core Sonar is not a track source.
- Test-first: tests are written and seen failing for the right reason before
  the code that satisfies them.

## Feature in progress

The state of the feature being worked on, so a session that ends mid-feature can
be picked up exactly where it stopped. Update it at every step change and
whenever progress within a step changes. When no feature is in progress, say so.

Feature: [M1-E track fusion and queue ingress](features/m1-e-track-fusion.md)
(requirements R1 to R37 with R16a, slices in section 21.5). Each slice goes
through tests then code with a gate after each.

| Step | State | Detail |
|---|---|---|
| 1. Plan | Done, approved by the user on 2026-10-26 | Includes the bearing amendment (21.12) and the sequencing change (21.5) |
| 2. Tests | Slice 1 done and approved | Remaining: slices 2 to 5 |
| 3. Code | Slice 1 done, waiting for acceptance | Full solution 761 of 761 passing on Windows. Remaining: slices 2 to 6; the contracts draft in `features/m1-e-track-fusion-contracts-draft.md` is a starting point for slices 3 and 4 |

Progress: slice 1 (contracts) is complete. Gate: waiting for the user to accept
slice 1, then the order to write the slice 2 (estimator) tests.

## Last changes made

Feature and step: M1-E fusion rework, slice 1 Step 3 (code). Gate: the user
ordered the slice 1 implementation on 2026-10-26.

- Implemented `TrackSourceDescriptor.Validate` (`Tracks.cs`), the R16a rules in
  `TrackObservationRules.cs` and `TrackMotion.Predict` (`TrackMotion.cs`).
  Corrected the comments on `RadarNormalizer` and `RadarTargetReport`.
- `dotnet build Core.Application.proj`: no warnings or errors. `dotnet test
  Poseidon.slnx`: 761 of 761 passed (677 before, plus the 42 new tests counted
  twice by the solution run). Recorded in the feature file's ledger.

Earlier in the same session (open questions and slice 1 tests):

- The user approved the documentation restructuring (described below).
- Recorded in `features/m1-e-track-fusion.md`: the 200 m join and split distance
  and the declared peak report rates are kept as starting values and tuned
  later, because no real data has been available (21.4, 21.10, "Still open").
- Searched for civilian non-geodetic sources and recorded the findings and a
  proposed answer in the new section 21.11. In short: range-and-bearing sources
  are common (ARPA `TTM`, cameras) and need only own-ship conversion. Bearing-only
  civilian sources exist only in search and rescue (direction finders). No
  civilian source needs a bearings-only estimator. The proposal keeps the
  estimator in CmsTracks and leaves slice 1 unchanged.
- The user then decided bearing tracks move back into CoreTracks, and the
  estimator stays in CMS (D9). I wrote the plan amendment (section 21.12:
  R16a, R33 to R35, contract and slice changes, four questions) and updated
  `features/cms-backlog.md` to match.
- The user answered the four questions (bearings fuse into point tracks;
  ambiguity leaves a standalone Bearing track until the operator merges it or
  the ambiguity clears; losing range makes a Hybrid track; the line starts at
  the displayed own ship, otherwise the report's). I revised 21.12 to match.
- The user answered the last three: cross-fixing is CMS; bearings are
  supplemental and refine the point only when the track's ranged sources are
  all AIS; the angular gate suits cameras but not EW, so Core leaves a seam for
  a CMS EW method. Everything is folded into 21.1, 21.2, 21.4, 21.5 and 21.6.
- The user ordered Step 2. I wrote the slice 1 tests
  (`Tests/Shared/Contracts.Tests/TrackSourceDescriptorTests.cs`,
  `TrackMotionTests.cs`, new cases in `TrackObservationRulesTests.cs`, the
  rewritten radar no-pose test) and the compile-only stubs (`Tracks.cs`,
  `TrackMotion.cs`), adjusted one superseded manager test, and moved the
  `Track` reshape to slice 3 and the relay and ingestion removal to slice 4
  (21.5).

Earlier in the same session (documentation restructuring, approved):

- Created [development-workflow.md](development-workflow.md) (plan, tests, code,
  gates, this resume context, templates).
- Split the former single plan `2026-10-24-poseidon-application-plan.md` into
  [poseidon-master-plan.md](poseidon-master-plan.md) and six feature files under
  `features/`: M1-A, M1-B, M1-C, M1-E as built, M1-E fusion rework, and the CMS
  backlog, plus the saved contracts draft for the fusion rework. The old file
  was removed once its content had moved (its history is in git).
- Moved `Reference/Data/Feature-Mappings` (the chart input data format
  mappings) to `Reference/Data/Plan-Memory/Feature-Mappings` and referenced it in
  the master plan, the M1-C file and the README.
- Added the "Feature in progress" section to this file and to the workflow
  template.
- The README now links to the master plan.
- The code is unchanged by this work. The verified state of the code (which
  also includes the M1-E single-source track pipeline and Display tracks
  overlay) is: `dotnet build Core.Application.proj` succeeded with no warnings
  and `dotnet test Poseidon.slnx` passed 677 tests with no failures on
  2026-10-26. That verification predates the documentation changes.
- Everything is uncommitted: the M1-E code and tests (new contracts, manager,
  relay, adapters, Display overlay, integration tests) and all the
  documentation changes. Only the earlier Node 24 workflow update and review
  fixes were merged before this session's later work.

## What needs to be done next

1. **Slice 1 acceptance.** The user accepts slice 1 (contracts).
2. **Slice 2 (estimator), Step 2 (tests)** on the user's order. Write numeric
   tests for frame conversion and the Kalman filter: convergence, weighting,
   late updates, rebasing far from the origin, the scalar bearing update (only
   while every Active ranged contribution is AIS, R35) and hybrid
   extrapolation with growing uncertainty (R36). See
   [features/m1-e-track-fusion.md](features/m1-e-track-fusion.md) sections 21.4
   and 21.5. The saved draft of the new contracts is in
   [features/m1-e-track-fusion-contracts-draft.md](features/m1-e-track-fusion-contracts-draft.md).
2. Tuning (later, when real data is available): the 200 m join and split
   distance, the declared peak report rates and the other starting values in
   section 21.4.
3. M1-E as built is not accepted until the rework is done and CI passes on
   Windows, Arch Linux and RHEL-compatible containers; the M1-C and M1-F
   planning has not started.

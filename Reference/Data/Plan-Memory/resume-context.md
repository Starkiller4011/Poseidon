# Resume context

Updated: 2026-10-31 (slice 5 committed, PR CI green on all three platforms, slice 6 close-out done and presented for acceptance)

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
| 1. Plan | Done, approved by the user on 2026-10-26 | Includes the bearing amendment (21.12), the sequencing change (21.5) and the multi-sensor-channel amendment (21.13, 2026-10-28) |
| 2. Tests | Slices 1 to 5 done and approved; slice 6 has no tests of its own (close-out) | Done |
| 3. Code | Slices 1 to 5 merged/accepted; slice 6 (close-out) done and presented | Waiting on the user's acceptance and the PR merge. The contracts draft in `features/m1-e-track-fusion-contracts-draft.md` is a starting point but predates slices 3 to 5 |

Progress: slice 5 (consumers and integration) is accepted by the user on
2026-10-29: `ObservationRelay`/`ITrackIngestion` are removed outright (no
shim); `CoreAIS`/`CoreRadar` are rewired onto a new `TrackSourceConnector`/
`ITrackPublisher` (per-channel for Radar), with a real background
registration/retry loop (`Start`/`StopAsync`); `ChartViewport.BearingLines`
(R37) does the real Bearing/Hybrid filtering; the real-runtime AIS+radar
fusion scenario (R27, R28) passes. Full design, the two bugs found and
fixed, and the triple-repeated passing run are in the feature file's "Slice
5 (consumers and integration)" Step 2 and Step 3 ledger entries, not
repeated here. Committed on 2026-10-31 (`ee0e486`).

Its PR then failed cross-platform CI (Windows, Arch Linux, RHEL-compatible)
across four rounds of fixes, each a distinct cause: a 15s wait margin too
tight for a loaded CI runner; a real registration-race gap (no retry) after
a module reload; cross-process test-execution contention from `dotnet
test`'s default parallel project execution (fixed with `-maxCpuCount:1` in
both CI workflows); and a real drain/pump TOCTOU race in
`TracksModuleTests.cs`'s test helper, exposed once CI runs became fully
serial. All fixed and pushed 2026-10-31; CI is now green on all three
platforms. Full detail in the feature file's "Slice 6 (close-out):
cross-platform CI failures on the slice 5 PR, fixed 2026-10-31" entry.

Slice 6 (close-out) itself is now also done, per 21.5 item 6: a final full
solution run (430 unique tests, zero failures, Windows), a consolidated
requirement-to-test ledger (R1 to R40 with R16a, every row a real passing
test) and a "known gaps and follow-ups" list superseding the pre-rework
pipeline's own, both in the feature file's new "Slice 6 (close-out):
requirement ledger, known limits and final verification, 2026-10-31" entry.
The PR already has green cross-platform CI from the fix pass above. Gate:
presented to the user; M1-E rework is complete once they accept it and the
PR is merged.

A second feature is now also in flight, by the user's explicit choice to
interleave them: [sensor simulators and ComputerGeneratedForce](features/sensor-simulators.md)
(a `Simulators/` solution folder of standalone sensor-simulator processes, a
`ComputerGeneratedForce` world server, and a new wire-protocol listener in
`CoreAIS`/`CoreRadar`/`CoreSonar`).

| Step | State | Detail |
|---|---|---|
| 1. Plan | Done, approved by the user on 2026-10-27 | Includes the `NdsSonarSim` civilian correction |
| 2. Tests | Not started | May proceed alongside M1-E slices 3 to 6, on the user's order |
| 3. Code | Not started | Waits until M1-E (slices 3 to 6) is done |

Progress: plan approved. Gate: waiting for the order to start Step 2 (tests)
for either feature (M1-E slice 3, or sensor-simulators, or both).

## Last changes made

Feature and step: M1-E fusion rework, slice 5 (consumers and integration)
accepted 2026-10-29, on branch `feature/core-track-system`. Slice 6 has not
started.

- The user accepted slice 5 on 2026-10-29 (tests approved the same day,
  Step 3 code written and accepted the same day). Implemented
  `TrackSourceConnector.Start`/`StopAsync` for real: a
  background task (execution-context flow suppressed) retries
  `ITrackPublisher.Register` every 200 ms until it succeeds, then holds the
  sink; `StopAsync` cancels it and disposes the sink. A descriptor update
  that lands in the registration race window is reconciled once the sink
  is set, so a rotation-rate change is never silently lost. Implemented
  `ChartViewport.BearingLines` for real: one line per Bearing-/Hybrid-kind
  track, styled by that track's own state.
- Found and fixed two bugs while making the tests pass: (1) `AisModule`/
  `RadarModule.Submit` incremented their sequence counter before checking
  the connector had actually registered, so a report rejected as
  `Unavailable` in the registration window still permanently burned a
  sequence number — fixed by checking `Sink is null` first; (2) three
  adapter tests asserted on the very first `Submit`/descriptor read
  immediately after `StartAsync`, assuming registration had already
  completed, which is a race now that it is a real background task — fixed
  by polling past it (mechanical, same category as slice 4's `DrainOnce`
  fixes), not by changing what they check.
- `dotnet build Poseidon.slnx`: no warnings or errors. `dotnet test
  Poseidon.slnx` run three times in a row: every project passes with zero
  failures each time, including both aggregates (no MSB4181 recurrence) and
  `CoreUI.Tests`' `ShellViewportIntegrationTests`. Full detail in the
  feature file's "Slice 5 (consumers and integration)" Step 3 ledger entry.
  Nothing is committed.

Earlier in the same session: slice 5 Step 2 (tests) was written against the
two stubs above and failed there, uncaught (16 tests); full detail is in
the feature file's own Step 2 ledger entry, not repeated here.

Earlier in the same session: slice 4 (ingress) accepted on 2026-10-28, on
the explicit terms in its ledger entry's "not done in this pass" list,
which slice 5 above now picks up as its first step; full detail is in the
feature file's "Slice 4 (ingress)" Step 2 and Step 3 ledger entries, not
repeated here.

Earlier in the same session: slice 3 (manager) accepted on 2026-10-28.

- Implemented `TrackManager` (correlation, contributions, per-contribution
  lifecycle, automatic split, operator merge/split, bearing association and
  kind changes, checkpoint schema 2) and added `Peek`/`Export`/`Restore` to
  `TrackEstimator` (additive; no slice 2 test's behavior changed). Full
  design in the feature file's Step 3 ledger entry, including the gating/
  dominance-score simplifications (isotropic uncertainty, position-only
  likelihood) documented as starting points.
- Found and fixed six test-design bugs while making the tests pass (detailed
  in the ledger, not repeated here), plus one real production bug: rejoining
  a source whose earlier contribution was Lost (R15) added the new
  contribution without removing the old one, leaving both; fixed in
  `ApplyObservation`.
- `dotnet build Poseidon.slnx`: no warnings or errors. `dotnet test
  Tests/Core/CoreTracks.Tests/CoreTracks.Tests.csproj`: 63 of 63 passed.
  `dotnet test Poseidon.slnx`: every project passed with zero failures,
  including `CoreUI.Tests` (28/28, the previously timing-out
  `ShellViewportIntegrationTests` test now passes) and both aggregates
  (no MSB4181 this time). Recorded in the feature file's ledger.
- Nothing is committed yet (working tree has the new and edited files,
  uncommitted, on `feature/core-track-system`).

Earlier in the same session: slice 3 Step 2 (tests) was written against a
stub `TrackManager` and failed as expected; full detail is in the feature
file's own Step 2 ledger entry, not repeated here.

Earlier in the same session (sensor-simulators plan, approved, no further
work since):

- Wrote the plan [features/sensor-simulators.md](features/sensor-simulators.md)
  for the `Simulators/` solution folder (13 standalone console-app project
  skeletons the user added outside this session: `AisSim`,
  `ComputerGeneratedForce`, `DataLinkSim`, `EchoSounderSim`, `EwSim`,
  `FireControlRadarSim`, `GpsSim`, `HullMountedSonarSim`,
  `NavigationRadarSim`, `NdsSonarSim`, `SurveillanceRadarSim`,
  `TowedArraySonarSim`, `VariableDepthSonarSim`; none yet added to
  `Poseidon.slnx`). The plan covers civilian simulators first (`AisSim`,
  `NavigationRadarSim`, `EchoSounderSim`, `NdsSonarSim`, `GpsSim` — corrected
  2026-10-27: `NdsSonarSim` is a civilian fish-finding/seabed-mapping sonar,
  not military) plus `ComputerGeneratedForce` as a live world server,
  wire-protocol simulated connections end to end, and the new Poseidon-side
  listeners this requires in `CoreAIS`/`CoreRadar`/`CoreSonar`. The remaining
  7 CMS/military Sim projects stay reserved, empty scaffolding until the CMS
  backlog starts. Linked from the master plan's features table.
- Nothing else is committed yet. `Simulators/` is untracked (`git status`);
  the user has not asked for it to be added or committed.

Earlier in the same session: M1-E slice 2 (estimator) was implemented and
accepted, including two test corrections found while implementing it; full
detail is in the feature file's own Step 3 ledger entry, not repeated here.

Earlier (before this session; slice 1, now merged):

- M1-E fusion slice 1 (contracts: `TrackSourceDescriptor.Validate`, the R16a
  rules in `TrackObservationRules`, `TrackMotion.Predict`) was implemented,
  reviewed and merged into `dev` as PR #2 (`8f9ef10`, `8c67e3a`, merge
  `e060785`). The decisions behind it (the bearing-tracks-in-Core amendment,
  D9, 21.12) are recorded in the feature file, not repeated here.

## What needs to be done next

1. **User acceptance of slice 6 (close-out) and the PR merge.** Everything
   21.5 item 6 asks for is done: full solution run (430 unique tests, zero
   failures), the consolidated requirement-to-test ledger, the known-gaps
   documentation, and green cross-platform CI on the slice 5 PR. M1-E
   rework is complete once the user accepts and the PR is merged.
2. **Sensor-simulators Step 2 (tests)**, on the user's order, may now
   proceed (plan approved 2026-10-27, M1-E slices 5/6 are done); its Step 3
   (code) still waits until M1-E is fully accepted and merged.
3. Tuning (later, when real data is available): the 200 m join and split
   distance, the declared peak report rates and the other starting values
   in section 21.4 — explicitly deferred, not a blocker for acceptance.
4. The M1-C and M1-F planning has not started.

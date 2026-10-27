# Resume context

Updated: 2026-10-27 (slice 3 tests written)

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
| 2. Tests | Slices 1 and 2 done and approved; slice 3 done, awaiting approval | Next: slices 4 to 6 |
| 3. Code | Slice 1 merged (PR #2); slice 2 coded and accepted | Remaining: slices 3 to 6; the contracts draft in `features/m1-e-track-fusion-contracts-draft.md` is a starting point for slices 3 and 4 |

Progress: slice 3 (manager) Step 2 tests are written
(`Tests/Core/CoreTracks.Tests/TrackManagerTests.cs`,
`Tests/Core/CoreTracks.Tests/TrackBearingTests.cs`) against a stub
`TrackManager`, and fail (46 of 63 in CoreTracks.Tests, including 8 expected
collateral failures in other files) as expected. Gate: waiting for the user to
accept the slice 3 tests, then the order to implement slice 3 (Step 3).

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

Feature and step: M1-E fusion rework, slice 3 (manager) Step 2 (tests), on
branch `feature/core-track-system`. The sensor-simulators plan (above) was
approved earlier in the same session but has had no further work since.

- Reshaped the contracts for fusion (21.2, 21.12): `Track` now has `Kind`
  (`Point`/`Bearing`/`Hybrid`), a nullable `Bearing` and `Contributions`
  (replacing `SourceId`/`SourceTargetId`/`LastSequence`); added
  `ITrackOperations`/`TrackOperationResult`. `TrackPolicy` is reshaped into
  unregistered-source fallbacks plus the 21.4 correlation/estimation
  tunables; added an internal `RegisterSource` entry point on `TrackManager`
  (R11).
- Rewrote `System/Core/CoreTracks/TrackManager.cs` as a Step 2 stub:
  `ITrackIngestion`, `ITrackFeed` and now `ITrackOperations`, every
  behavior-bearing member throwing `NotImplementedException` except
  `Advance()` (a no-op, since it runs on a background timer and throwing
  there would crash it rather than fail a test) and the read-only
  `GetSnapshot`/`GetChanges` (fixed empty result).
- Wrote `Tests/Core/CoreTracks.Tests/TrackManagerTests.cs` (rewritten,
  dropping the superseded "nearby positions stay separate" premise) and the
  new `Tests/Core/CoreTracks.Tests/TrackBearingTests.cs`, covering R1 to R21
  and R33 to R36.
- Fixed compile-only `Track(...)` constructions in `TrackMotionTests.cs`,
  `TrackReplicaTests.cs` and `CoreDisplay.Tests/TrackLayerTests.cs` for the
  new shape (no behavior change); trimmed
  `TrackPipelineIntegrationTests.cs`'s AIS+radar scenario to its still-valid
  AIS-only path, deferring the real fusion scenario to slice 5 (R27, R28).
- `dotnet build Poseidon.slnx`: no warnings or errors. `dotnet test
  Tests/Core/CoreTracks.Tests/CoreTracks.Tests.csproj`: 46 failed of 63 (the
  38 new tests, plus 8 expected collateral failures in `TracksModuleTests`,
  the shared `ModuleDeclarationTests` and `TrackPipelineIntegrationTests`,
  all attributable to the same stub). `Contracts.Tests` (105/105) and
  `CoreDisplay.Tests` (18/18) unaffected. Full `dotnet test Poseidon.slnx`
  confirmed: every project passes except `CoreTracks.Tests` (as above) and
  one more collateral failure, `CoreUI.Tests.ShellViewportIntegrationTests.
  AisInput_ReachesTheShellViewportThroughTracksAndDisplay` (1 of 28, same
  timeout cause); the Civilian aggregate hits the same MSB4181 collateral as
  slice 2's failing run. Recorded in the feature file's ledger.
- Nothing is committed yet (working tree has the new and edited files,
  uncommitted, on `feature/core-track-system`).

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

1. **Slice 3 (manager) acceptance.** The user accepts the tests in
   `Tests/Core/CoreTracks.Tests/TrackManagerTests.cs` and `TrackBearingTests.cs`
   and the contract/policy reshape they assume (feature file ledger, "Slice 3
   (manager): Step 2"). The full `dotnet test Poseidon.slnx` run is confirmed
   clean apart from the attributable collateral already recorded there.
2. **Slice 3 Step 3 (code)**, on the user's order: implement `TrackManager`
   (likely split into `TrackManager`, `TrackCorrelator`, `TrackPolicy` per
   21.3) so the 38 slice 3 tests pass without changing them, then run the
   focused tests, the full solution, and record the passing run.
3. **Sensor-simulators Step 2 (tests)**, on the user's order, may proceed
   alongside the above (plan approved 2026-10-27); its Step 3 (code) still
   waits until M1-E (slices 3 to 6) is done.
4. Slices 4 to 6 of M1-E (ingress, consumers, close-out) have not started; see
   21.5.
5. Tuning (later, when real data is available): the 200 m join and split
   distance, the declared peak report rates and the other starting values in
   section 21.4.
6. M1-E as built is not accepted until the rework is done and CI passes on
   Windows, Arch Linux and RHEL-compatible containers; the M1-C and M1-F
   planning has not started.

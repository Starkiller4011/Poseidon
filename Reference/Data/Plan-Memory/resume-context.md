# Resume context

Updated: 2026-11-01 (M1-E fusion rework accepted and merged, PR #7/`c01fd22`; sensor-simulators deferred; M1-H code done and passing, awaiting acceptance; M1-I plan approved, Step 2 tests written and confirmed failing, awaiting test-gate approval)

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

Feature: [M1-H: CoreUI extensibility, layout and input devices](features/m1-h-ui-extensibility.md)
(its own milestone now, depending on M1-B; requirements H1 to H23). The
chart region (hosting Display's viewport, unchanged from B5-B9) and a new
always-visible side-panel area (`coreui.sidepanel`, e.g. track information)
sit next to a separate paged control-pages area (`coreui.page`/
`coreui.panel`, sensor/navigation controls), hosted per a startup
hardware-configuration file: one window (chart+sidebar+pages) in `Single`
mode, two windows (chart+sidebar; pages) in `Dual` mode. Keyboard shortcuts
are a new `IModuleHost` (Core-level, not CoreUI-owned) mechanism:
`RegisterShortcut`/`SetActiveShortcutScope`/`DispatchShortcut`. CoreUI also
owns a separate TID touch window for buttonology (plain resizable window
for now; fullscreen on real hardware is a later follow-up). Diagnostics/
reload/exit are removed from CoreUI's shell entirely, deferred to a future,
unplanned configuration-editing tool that will also own the
hardware-configuration file.

| Step | State | Detail |
|---|---|---|
| 1. Plan | Done, approved by the user on 2026-11-01 | Two rounds of feedback (chart-must-always-be-visible plus single-/dual-display hardware configs, then answers to all 5 open questions); filed as its own milestone (M1-H) rather than an M1-B continuation |
| 2. Tests | Done, approved by the user on 2026-11-01 | H1 to H23 all covered (55 CoreUI.Tests + 7 Core.Tests cases after 3 `FormatPackage`-related cases were deleted as dead code); every new test confirmed failing for the right reason |
| 3. Code | Done, presented for the gate | All 23 requirements implemented; `dotnet test Poseidon.slnx -maxCpuCount:1` green with zero failures, 2026-11-01 |

Progress: `ConsoleHardwareConfiguration.Load`, `ModuleRuntime`'s shortcut
registry, and a full `CoreUiModule` rewrite (hardware-mode-driven layout,
side panels, control pages, TID, keyboard forwarding) are all implemented
for real. One real bug found and fixed (a `StackPanel` holding a released
panel's view never had its own `Children` cleared, so the view's `Parent`
never became null — `RenderControlPagesContentAsync` in `CoreUiModule.cs`).
Three existing tests needed mechanical updates for legitimate behavior
changes (a race in two `ShellLayoutIntegrationTests` cases, a missing
`console.json` and a stale "exactly one window" assumption in two
`ShellViewportIntegrationTests` cases now that CoreUI also opens a TID
window). Full detail, including the three tests revised for H12's removed
diagnostics panel, is in the feature file's Step 3 ledger entry, not
repeated here. Gate: waiting for the user's acceptance to close out M1-H.

A second feature is now also in flight, by the user's explicit choice to
interleave them (M1-H's own acceptance is still outstanding, see above):
[M1-I: module logging](features/m1-i-module-logging.md), a shared `ILog`
facility for every module and the host, with a `logging.json` config flag
for debug-level logging, a `POSEIDON_LOG_LEVEL` environment-variable
override, and two Visual Studio run profiles (Normal/Debug) to flip it
without editing the file.

| Step | State | Detail |
|---|---|---|
| 1. Plan | Done, approved by the user on 2026-11-01 | Custom `ILog` (cross-platform compatibility), console+file output, `logging.json`, plus the user's follow-on ask for VS run-profile switching (answered with `POSEIDON_LOG_LEVEL` + `launchSettings.json`) |
| 2. Tests | Done, presented for the gate | I1 to I12 all covered (18 new Core.Tests cases; I9 by regression guard, I12 by manual verification); every new test confirmed failing for the right reason |
| 3. Code | Not started | Waits on the Step 2 gate |

Progress: tests written, run, and confirmed failing for the right reason.
`Core.Tests` is the only project with failures (141 cases: 123 pre-existing
unaffected, 18 new all failing as expected); the rest of the solution
(`dotnet test Poseidon.slnx -maxCpuCount:1`) is green. The shared
`FixtureModule`/`RuntimeTest` test fixture gained a `UseLogDuringStartAndStop`
flag and a `LogDirectory`, used to exercise `GetLog` through a real
module's lifecycle methods — additive, no other test broke. Full detail,
including the requirement-to-test ledger, is in the feature file's Step 2
entry, not repeated here. Gate: waiting for the user's explicit order to
continue to Step 3 (code).

[M1-E track fusion and queue ingress](features/m1-e-track-fusion.md)
(requirements R1 to R40 with R16a) is done: all 6 slices accepted, PR #7
merged into `dev` as `c01fd22` on 2026-11-01, with green cross-platform CI.

[Sensor simulators and ComputerGeneratedForce](features/sensor-simulators.md)
is plan-approved (2026-10-27) but deferred by the user's explicit choice on
2026-11-01: paused with no tests or code started, to be picked up later.

## Last changes made

M1-I Step 2 (tests): wrote and ran the tests for all 12 requirements
(I1 to I12) on branch `feature/core-track-system`, confirmed failing for
the right reason, not committed yet. New files: `LoggingConfigurationTests.cs`,
`LogSinkTests.cs`, `ModuleLoggingTests.cs` (18 cases total) in `Core.Tests`.
Production stubs added: `Poseidon.Contracts`' `ILog`/`LogLevel`/`LogExtensions`
(real, no behavior to stub) and `IModuleHost.GetLog` (throws by default,
same pattern as M1-H's shortcut members); `Poseidon.Core.Runtime.Logging`'s
`LoggingConfiguration.ResolveMinimumLevel` and `LogSink.Write` (throw);
`ModuleRuntime` gained an unused `logDirectory` constructor parameter for
Step 3 to wire up. The shared `FixtureModule`/`RuntimeTest` test fixture
(used by other `Core.Tests` cases too) gained an additive
`UseLogDuringStartAndStop` flag and `LogDirectory`, confirmed not to break
anything else. `dotnet build Poseidon.slnx`: no warnings or errors.
`dotnet test Poseidon.slnx -maxCpuCount:1`: `Core.Tests` is the only
project with failures (123 pre-existing pass, 18 new fail as expected);
every other project is green.

Earlier in the same session: updated the `feedback_commit_message_after_dev_steps`
memory — the user corrected it (2026-11-01) to stop asking to commit or
offering a commit action at all, not just to surface the message
unprompted; he handles all git operations himself. Then drafted and finalized the Step 1 plan for
[M1-I: module logging](features/m1-i-module-logging.md) (a shared `ILog`
facility, `IModuleHost.GetLog`, console+file output, a `logging.json`
debug-level flag). The user confirmed all 3 open questions (custom `ILog`,
both console and file output, `logging.json`) and added one more ask: two
Visual Studio run targets to switch between normal and debug logging —
resolved with a `POSEIDON_LOG_LEVEL` environment-variable override (wins
over `logging.json` when set) plus a new
`System/Core/Core/Properties/launchSettings.json` with "Poseidon" and
"Poseidon (Debug logging)" profiles.

Earlier in the same session: M1-H Step 3 (code): implemented all 23 requirements (H1 to H23) for real
on branch `feature/core-track-system`, not committed yet.
`ConsoleHardwareConfiguration.Load` parses `console.json` for real (same
shape as `RadarHardwareConfiguration`); `ModuleRuntime` gained a real
shortcut registry backing the three `IModuleHost` members added in Step 2;
`CoreUiModule` was rewritten in full — hardware-mode-driven window layout
(chart+side-panels window, plus a control-pages window in `Dual` mode, plus
the TID window), the control-pages region (page switcher, duplicate/orphan
diagnostics, release discipline), and keyboard forwarding. One real bug
found and fixed while making a test pass: a `StackPanel` wrapping a
released control-page panel's view was detached from the window but never
had its own `Children` cleared, so the view's `Parent` stayed pointing at
the orphaned `StackPanel` instead of becoming null — fixed in
`RenderControlPagesContentAsync`. Three existing tests needed mechanical
updates for legitimate new behavior (not pre-existing bugs): a race in two
`ShellLayoutIntegrationTests` cases (window count could satisfy a check
before the first refresh tick populated viewport text); two
`ShellViewportIntegrationTests` cases needed a `console.json` written for
the real CoreUI package they deploy (CoreUI now requires one to start) and
needed to stop assuming "exactly one visible window" now that CoreUI also
opens the TID window. `dotnet build Poseidon.slnx` and
`dotnet build Core.Application.proj`: no warnings or errors.
`dotnet test Poseidon.slnx -maxCpuCount:1`: every project "Test Run
Successful", zero failures, including `CoreUI.Tests` (55/55), `Core.Tests`
(123/123) and `Contracts.Tests` (94/94). Full detail, including the
requirement-to-test ledger, is in the feature file's Step 3 entry, not
repeated here.

Earlier in the same session: M1-E fusion rework's PR (#7, `feature/core-track-system` into `dev`) was
accepted by the user and merged 2026-11-01 as `c01fd22`. A Windows-only CI
recurrence was fixed first and included in the merge: the "Collect civilian
coverage" step in `build-test-package.yml` ran its own separate `dotnet
test` invocation that never got the `-maxCpuCount:1` flag added during
slice 6 close-out, reproducing the same cross-process contention and timing
out `SourceDisconnect_IsVisibleInTheFeed_AndAnObservationRestoresIt` again;
fixed by adding the flag there too (CI-script only, committed as `88d56c0`,
documented in the feature file's slice 6 section). The user also chose to
defer sensor-simulators rather than start its Step 2 (tests) now.

Earlier in the same session: M1-E fusion rework, slice 5 (consumers and
integration) accepted 2026-10-29, on branch `feature/core-track-system`.

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

1. **Get the user's acceptance of [M1-H's code](features/m1-h-ui-extensibility.md)**
   to close out the milestone; the full passing run and the bug found
   along the way are recorded and waiting for review. Nothing is
   committed yet.
2. **Get the user's approval of [M1-I's tests](features/m1-i-module-logging.md)**
   to move past the Step 2 gate into Step 3 (code); the requirement-to-test
   ledger and the failing run are recorded and waiting for review.

Everything else waiting, unblocked by anything above and not in
competition with it, per the master plan's milestone table:

3. **M1-C: Chart-data pipeline.** Not started, no plan approved. Needs
   M1-A only (done), so it can be planned now. Audit S-101/S-158/S-164
   specifications and pin fixture versions; Charts decodes a real base
   cell; verify metadata, attributes, topology and malformed-input
   diagnostics.
4. **M1-D: Integrated chart view.** Not started; needs M1-B (done) and
   M1-C (not started), so it waits on M1-C. Display draws Charts data;
   pan/zoom, locked base, overlays, contour slider, feature inspection;
   this is also where `ChartViewport.BearingLines` (M1-E's R37) finally
   gets pixels on screen, a gap M1-E's close-out already flagged.
5. **M1-F: Navigation functions.** Not started; needs M1-E (done, so this
   is now unblocked). Own-ship state, tested CPA/TCPA, Display
   annotations, explicit unavailable/stale/degenerate-motion behavior.
6. **M1-G: Integration gate.** Not started; needs M1-D, M1-E, M1-F and
   M1-H, so it waits on all four. Cross-platform publish and native
   desktop smoke tests, actual civilian module suites, dependency/
   override/hot-reload failure tests across the integrated application.
7. **Sensor-simulators**, deferred — picks back up on the user's order
   (plan already approved 2026-10-27; Step 2, tests, is what resumes).
8. Tuning (later, when real data is available): M1-E's 200 m join/split
   distance, declared peak report rates and the other starting values in
   section 21.4 — explicitly deferred, not a blocker on anything above.

M1-C and M1-F have no plan yet and no dependency on each other, so either
can be planned next; M1-D and M1-G both wait on earlier work (M1-G also now
waits on M1-H).

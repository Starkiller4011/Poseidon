# Resume context

Updated: 2026-10-27 (slice 2 coded)

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
| 2. Tests | Slice 1 and slice 2 done and approved | Next: slice 3 (manager), on the user's order |
| 3. Code | Slice 1 merged (PR #2); slice 2 coded and accepted | Remaining: slices 3 to 6; the contracts draft in `features/m1-e-track-fusion-contracts-draft.md` is a starting point for slices 3 and 4 |

Progress: slice 2 (estimator) is done and accepted. Gate: waiting for the
order to start slice 3 (manager/correlator) Step 2 (tests).

## Last changes made

Feature and step: M1-E fusion rework, slice 2 accepted (Step 3 code already
done), on branch `feature/core-track-system`.

- Implemented `TrackEstimator` (`System/Core/CoreTracks/TrackEstimator.cs`) to
  the Step 2 design: the 4-state local east/north Kalman filter, sequential
  scalar position/velocity updates, process noise, the bearing update gated on
  an active ranged contribution, the clock that never moves backward, and the
  ~20 km frame re-anchor.
- Found and corrected two wrong assumptions in the slice 2 tests themselves
  (not the design or requirements) while making them pass: a convergence
  assertion compared a two-axis combined (DRMS) uncertainty against a one-axis
  figure, and a hybrid-extrapolation assertion dead-reckoned against an
  unconverged single-report velocity with a physically inconsistent
  (non-moving) position input. Both are detailed, with the reasoning, in the
  feature file's Step 3 ledger entry.
- `dotnet build Core.Application.proj`: no warnings or errors. `dotnet test
  Tests/Core/CoreTracks.Tests/CoreTracks.Tests.csproj`: 41 of 41 passed.
  `dotnet test Poseidon.slnx`: every test project passed with zero failures,
  including the Civilian and Military aggregates and the CMS smoke projects.
  Recorded in the feature file's ledger.
- The user accepted slice 2, including the two test corrections, on
  2026-10-27.
- Nothing is committed yet (working tree has the new and edited files,
  uncommitted, on `feature/core-track-system`).

Earlier (before this session; slice 1, now merged):

- M1-E fusion slice 1 (contracts: `TrackSourceDescriptor.Validate`, the R16a
  rules in `TrackObservationRules`, `TrackMotion.Predict`) was implemented,
  reviewed and merged into `dev` as PR #2 (`8f9ef10`, `8c67e3a`, merge
  `e060785`). The decisions behind it (the bearing-tracks-in-Core amendment,
  D9, 21.12) are recorded in the feature file, not repeated here.

## What needs to be done next

1. **Slice 3 (manager) Step 2 (tests)**, on the user's order: the correlator
   (including bearing association), contributions, lifecycle, kind changes,
   automatic split, operations and checkpoint, against R1 to R21 and R33 to
   R36 (21.5 item 3). The test file is rewritten, not patched, around the
   reshaped `Track`.
2. Slices 4 to 6 (ingress, consumers, close-out) have not started; see 21.5.
3. Tuning (later, when real data is available): the 200 m join and split
   distance, the declared peak report rates and the other starting values in
   section 21.4.
4. M1-E as built is not accepted until the rework is done and CI passes on
   Windows, Arch Linux and RHEL-compatible containers; the M1-C and M1-F
   planning has not started.

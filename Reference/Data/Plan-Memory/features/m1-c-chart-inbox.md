# Chart inbox and development chart staging

Milestone: M1-C (follow-on to [the chart-data pipeline](m1-c-chart-data-pipeline.md), done)
Status: Awaiting code approval (gate item resolved)

## Problem and scope

The chart library (`IChartLibrary.ImportAsync`) can import an exchange set,
but nothing in the application calls it. The store beside the host
(`charts/charts.db`) is where imports are written, not a folder the
application reads charts from. So a developer cannot run the application with
real charts in it, and production has no way to receive charts.

This feature adds the first production import path and feeds it from Debug
builds:

- **Chart inbox (production).** Charts checks an inbox folder when it starts.
  Every exchange set placed there (copied from removable media, delivered
  by a chart-service sync, or staged by a Debug build) is imported at the
  next start. The inbox is not monitored while the application runs. A
  processed set stays where it is. The store remembers it and does not
  import it again unless it changes.
- **Development staging (Debug only).** A Debug build of the application
  copies chosen exchange sets into the inbox, so the very path production
  uses imports them on launch.

Non-goals:
- An operator "Load charts" command, a load report window, or any chart
  drawing. The operator command comes with the chart UI in M1-D and calls
  the same import, so seeing the charts waits for M1-D. Until then, the
  imported charts show in the log and through `IChartLibrary` queries.
- Configurable store and inbox locations. Both stay beside the host
  executable, which is already a recorded follow-up of the pipeline
  feature (an installed deployment may make that folder read-only).
- Zipped exchange sets in the inbox. Delivered media carries an unzipped
  `S100_ROOT`; the Debug staging extracts the one zipped fixture itself.
- Permits, licences, and certificate trust (pipeline Q7 stands).

## Design

**Inbox scan (new, `System/Core/CoreCharts/Inbox/`).** It runs once per
start of `ChartsModule`: `StartAsync` starts it in the background, and
`StopAsync` cancels it if it is still running and waits for it to stop.

The scan works like this:
1. Discover the exchange sets: every folder under the inbox holding a
   `CATALOG.XML` (case-insensitive). The search does not descend into a set
   it has found, and is bounded in depth.
2. Take each set's listing: relative path, size and last-write time of every
   file in the set.
3. Skip any set whose listing matches the record of an earlier completed
   import.
4. Import the remaining sets one at a time, in path order, through the
   library's `ImportAsync`.

**Sets still being copied.** No settle interval is needed. A set still being
copied when the scan runs imports whatever is complete at that moment: a
truncated file fails its signature or structure check and is rejected, not
installed. Its listing will differ by the next start, so the set is imported
again then, and what is already installed is skipped (see idempotent import
below).

**Retry.** A set any of whose datasets was refused only for sequence (base
not installed, an update for a newer edition, a gap) is kept *pending*, not
completed. Within the scan, pending sets are retried after any import that
installs or changes a cell, until a round of retries changes nothing. Sets
still pending at the end are retried at the next start. This is needed
because real delivery order is not name order. In the SHOM data, the folder
of the edition 2 update `S101FR00369400_ER1_ed2_…` sorts before the edition
2 base `S101FR00369400_Ed2_…`, and every one of the four catalogues carries
the same issue date. So neither name nor date order imports them correctly.

**Idempotent import (library change).** For a re-tried or re-staged set to
be harmless, importing a dataset file whose exact bytes are already in the
installed cell's stored chain is skipped. It is reported as an Information
diagnostic, `already-installed`, rather than refused as out of sequence.
This also closes the window where a crash between publishing a snapshot and
writing the inbox record would make a restart re-import a set.

**Record.** `charts/inbox.json` sits beside the store and is replaced
atomically, like the manifest. Per set it holds the relative path, the
listing fingerprint, the state (completed or pending), the time, the status,
counts by severity, and every Error diagnostic.

**Locations (assumed).** All beside the host executable:
- Store: `charts/charts.db` (unchanged).
- Inbox: `charts/inbox/`, created at start if missing, so it is obvious
  where charts go.
- Record: `charts/inbox.json`.

**Logging.** `ChartsModule` gets its `ILog` from the host (`GetLog("Core.Charts")`)
in `StartAsync` and passes it to the scanner and the library. This also
closes the pipeline follow-up "the library's `ILog` is not wired".

**Debug staging (`CoreCharts.csproj`).** The precedent is CoreUI's Debug-only
`console.dev.json`. A Debug-only target runs after `DeployModule` and writes
into `$(ModuleDeploymentDirectory)\..\charts\inbox\`, the host directory's
inbox:
- `S-164-PowerUp/`: the released S-164 2.0.0 PowerUp set, extracted from
  the committed `S-164-Sub-Group` zip by a small inline task. The task
  normalises backslash entry names, as the test fixtures do, so it also
  works on Linux.
- `UKHO-GB-Apr26/`: `Reference/Data/S-101/S-101/S-101_GB_Apr26/S100_ROOT`,
  only when present.
- `SHOM-FR-June26/`: the four SHOM sets, only when present. Only files
  inside `S100_ROOT` folders are copied, with their paths below SHOM's
  `Shom Data 22 June 2026/S-101` folder. Each set is staged flat, as
  `NN-<set folder>/S100_ROOT`, numbered from 01 in the ordinal order of its
  source path, which is edition order. The staging owns this folder and
  removes anything else in it, such as an earlier layout. This was changed
  at the Step 3 gate: the first layout kept each set's whole path below
  `S-101`, and its deepest file reached 275 characters from this clone,
  past Windows' 260-character limit. The flat layout's deepest file is 217
  characters.

The source folders are MSBuild properties, `DevelopmentChartsUkhoSource`
and `DevelopmentChartsShomSource`, defaulting to the repository paths. The
tests point them at a missing folder to check the skip.

Missing local data is a build message, never an error. Unchanged files are
not recopied, and a re-copied but identical set imports as `already-installed`
per dataset. The licensed data is copied only into the git-ignored `bin`
output, never into the repository or a Release build.

Alternatives considered:
- A build-time pre-seeded store: no runtime import path, so it does not
  rehearse production.
- Copying files only: nothing would import them.
- Moving processed sets to `processed/` or `failed/`: every Debug build would
  re-stage and re-import them.
- Monitoring the inbox while running, with a watcher or a periodic rescan,
  plus a settle interval for sets being copied: not needed (user decision,
  2026-11-01). Checking at startup is enough.

## Requirements

| ID | Requirement |
|---|---|
| CI1 | Every folder under the inbox, up to 8 levels deep, that holds a `CATALOG.XML` (any case) is one exchange set; the search does not descend into a set it found; other files and folders are ignored; a missing inbox is created when Charts starts |
| CI2 | After a set's import completes, its listing fingerprint (relative paths, sizes, last-write times) and outcome are recorded durably beside the store; a set whose listing is unchanged is not imported again at later starts; a set with any file added, removed or changed is imported again at the next start |
| CI3 | Sets are imported one at a time in path order; a set with any dataset refused for sequence (`base-not-installed`, `update-edition-newer`, or a gap) stays pending and is retried within the same scan after any later import installs or changes a cell, until a round of retries changes nothing, and again at the next start if still pending. With the four SHOM sets in the inbox, `101FR00369400` ends at edition 2 update 1 and `101FR00571300` at edition 2 update 1, whatever their folder order |
| CI4 | Importing a dataset file whose exact bytes are already in the installed cell's stored chain is skipped with an Information diagnostic `already-installed`, not an error; an import in which every dataset is already installed returns `Loaded` with no cells and publishes no new generation |
| CI5 | The inbox is scanned once each time the Charts module starts, in the background, without `StartAsync` waiting for any import; it is not monitored while the module runs, so a set added while running is imported at the next start |
| CI6 | `StopAsync` cancels a scan or import still in progress and returns once it has stopped; the published chart is left intact (C25) and a cancelled set is not recorded, so it is imported at the next start; `StopAsync` is idempotent |
| CI7 | Each processed set's outcome (status, cells installed or changed, counts of diagnostics by severity, and every Error with its file) is written to the Charts log and kept in the record |
| CI8 | An unreadable inbox, an exception during the scan, or an unreadable or corrupt record is logged and never fails the module's start; a corrupt record is set aside and its sets are imported again (harmless per CI4) |
| CI9 | The store stays at `charts/charts.db`, the inbox is `charts/inbox/` and the record is `charts/inbox.json`, all beside the host executable (assumed; configurable locations remain a follow-up) |
| CI10 | A Debug build of the application stages into the host output's inbox the released S-164 2.0.0 PowerUp set (extracted from the committed zip, with backslash entry names normalised) and, where present on the machine, the UKHO `S-101_GB_Apr26` set and the four SHOM `S-101_FR_June26` sets; absent local data is skipped with a build message and never fails the build; files already staged unchanged are not copied again |
| CI11 | A Release build or publish stages no chart data and creates no inbox content |
| CI12 | Starting the Charts module over the Debug-staged inbox installs PowerUp's 6 cells and, where the local data is present, UKHO's 27 and SHOM's 2 (both at edition 2 update 1), with no Error diagnostics (UKHO's producer-typo support reference stays a Warning); a second start over the same inbox imports nothing |

## Decisions

- 2026-11-01: charts reach the library through an inbox folder now; an
  operator "Load charts" command comes later with the chart UI (M1-D) and
  calls the same import. The staging replicates the production path rather
  than shortcutting it.
- 2026-11-01: a processed set stays in the inbox; the store remembers it and
  imports it again only if it changes.
- 2026-11-01: Debug builds stage the released S-164 PowerUp set, the UKHO GB
  set and the SHOM FR sets (the latter two only where present locally).
- 2026-11-01: the inbox is checked only when Charts starts, not monitored at
  runtime. The settle interval and periodic rescan were dropped with it.

## Open questions

None blocking. The locations in CI9 are assumed and can be changed at this
gate.

## Ledger

**Step 2 (tests), 2026-11-01.** 31 new tests across four classes in
`Tests/Core/CoreCharts.Tests`:
- `ChartInboxTests`: drives `ChartInbox` directly, with a real library or a
  blocking or throwing stand-in.
- `ChartLibraryReimportTests`.
- `ChartsModuleInboxTests`: drives the module through `IModule`, with a
  host double that supplies the log.
- `ChartStagingTests`: runs the `StageDevelopmentCharts` target in a child
  `dotnet msbuild`, with an explicit configuration and a temporary host
  folder. This works whatever configuration the test run uses; CI runs
  Release.

"(local)" marks a test that needs the git-ignored UKHO/SHOM data.

Compile-only production stubs:
- `Inbox/ChartInbox.cs`: `ChartInbox`, `ChartInboxOptions`,
  `InboxSetRecord`, `InboxSetState`, `InboxScanResult`. Members throw
  `NotImplementedException`.
- `ChartDiagnosticCodes.AlreadyInstalled`.
- `ChartsModule`: a constructor taking the charts directory (the
  parameterless one passes `AppContext.BaseDirectory/charts`), a
  `ChartsDirectory` property, and a `StartupScan` that is always null.

Test support lives in `InboxSupport.cs`: a recording log, a host double,
inbox helpers, and the child-build runner.

Failing run: CoreCharts.Tests 30 failed, 175 passed, of 205. All 174
existing tests pass. Of the 31 new tests, 30 fail because the behaviour is
missing; each was checked in the TRX:
- 14 inbox tests: `NotImplementedException` from the stub.
- 2 re-import tests: the second import is still a re-issue that returns
  cells.
- 7 staging tests: `MSB4057`, the target does not exist; for
  `HostBuild_…`, nothing is staged in the Debug host output.
- 7 module tests: there is no startup scan, so `StartupScan` is null.

One new test passes already: `DefaultModule_KeepsItsChartsBesideTheHost`.
It checks the path plumbing the stub constructor needed in order to
compile, and stays as a guard. `HostBuild_StagesTheInboxOnlyInDebug`
checks the Release half when run in Release (as CI does), where it passes
already. Full solution (`dotnet test Poseidon.slnx -maxCpuCount:1`): every
other project passes. The Civilian aggregate reports `MSB4181` because of
CoreCharts' expected failures.

| Requirement | Test | Failing run (Step 2) | Passing run (Step 3) |
|---|---|---|---|
| CI1 | `ChartInboxTests.Scan_FindsEverySetUpToEightLevelsDeepWhateverTheCatalogueCase`, `Scan_CatalogueInsideAFoundSet_IsNotAnotherSet`; `ChartsModuleInboxTests.Start_WithoutAnInbox_CreatesIt` | NotImplementedException; module: no scan (null) | Pass (2026-11-01) |
| CI2 | `Scan_UnchangedCompletedSet_IsSkippedIncludingAfterARestart`, `Scan_SetWithAFileChanged_IsImportedAgain` ×3 (touched, added, removed) | NotImplementedException | Pass (2026-11-01) |
| CI3 | `Scan_UpdateBeforeItsBase_IsRetriedAfterTheBaseInstalls`, `Scan_UpdateWhoseBaseNeverArrives_StaysPendingAndIsRetriedAtTheNextStart`, `Scan_ShomSetsOutOfOrder_EndAtEdition2Update1` (local) | NotImplementedException | Pass (2026-11-01) |
| CI4 | `ChartLibraryReimportTests.Import_SameExchangeSetAgain_IsAlreadyInstalledAndPublishesNothing`, `Import_BaseOrUpdateAgainAfterTheUpdate_IsAlreadyInstalledNotOutOfSequence` | Re-import returns cells (treated as a re-issue) | Pass (2026-11-01) |
| CI5 | `ChartsModuleInboxTests.Start_ReturnsBeforeTheInboxIsImportedThenImportsItInTheBackground`, `SetAddedWhileRunning_IsImportedAtTheNextStart` | No scan (`StartupScan` null) | Pass (2026-11-01) |
| CI6 | `ChartInboxTests.Scan_Cancelled_ThrowsAndDoesNotRecordTheSetBeingImported`; `ChartsModuleInboxTests.Stop_DuringTheStartupScan_WaitsForItAndLeavesTheRecordConsistent` | NotImplementedException; no scan (null) | Pass (2026-11-01) |
| CI7 | `ChartInboxTests.Scan_RecordsAndLogsEachSetsOutcome`; `ChartsModuleInboxTests.Start_LogsEachSetsOutcomeToTheChartsLog` | NotImplementedException; no scan (null) | Pass (2026-11-01) |
| CI8 | `ChartInboxTests.Scan_CorruptRecord_IsLoggedSetAsideAndItsSetsImportedAgain`, `Scan_InboxThatCannotBeRead_IsLoggedAndImportsNothing`, `Scan_ImportThatThrows_IsLoggedAndTheOtherSetsStillImport`; `ChartsModuleInboxTests.Start_WithAnUnusableInbox_StillStartsAndLogsTheProblem` | NotImplementedException; no scan (null) | Pass (2026-11-01) |
| CI9 | `ChartsModuleInboxTests.DefaultModule_KeepsItsChartsBesideTheHost`, `Start_KeepsTheStoreInboxAndRecordInTheChartsDirectory` | First passes already (stub path plumbing); second: no scan (null) | Pass (2026-11-01) |
| CI10 | `ChartStagingTests.DebugStaging_ExtractsThePowerUpSetIntoTheInbox`, `DebugStaging_CopiesTheLocalUkhoAndShomSets` (local), `DebugStaging_WithoutTheLocalData_SkipsItWithAMessage`, `DebugStaging_RunAgain_DoesNotCopyUnchangedFiles`, `HostBuild_StagesTheInboxOnlyInDebug` | MSB4057 (no target); host build: nothing staged | Pass (2026-11-01) |
| CI11 | `ChartStagingTests.ReleaseStaging_StagesNothing`, `HostBuild_StagesTheInboxOnlyInDebug` (Release half) | MSB4057 (no target) | Pass (2026-11-01) |
| CI12 | `ChartStagingTests.StagedInbox_ImportsEverythingAtStartupAndNothingAtTheNextStart` (PowerUp everywhere; UKHO and SHOM where local) | MSB4057 (no target) | Pass (2026-11-01) |

## Status and follow-ups

Plan approved 2026-11-01 (revised for startup-only scanning); tests approved
the same day; code done and passing, at the Step 3 gate.

**Step 3 (code), 2026-11-01.**
- `Inbox/ChartInbox.cs`:
  - discovery: depth-bounded, not following links, stops at a set root;
  - listing fingerprints;
  - import one set at a time, with pending sets retried in rounds after
    any progress;
  - the record replaced atomically after every attempt; an unreadable
    record is set aside;
  - per-set log lines, plus one Error line per error.
- `ChartLibrary`:
  - a dataset whose bytes are already in its cell's stored chain is skipped
    with `already-installed` (Information);
  - a support file identical to the one kept is not a change, so a repeat
    import publishes no generation;
  - the library's `ILog` can be supplied after construction (`UseLog`).
  This closes the pipeline follow-up "the library's `ILog` is not wired".
- `ChartsModule`:
  - `StartAsync` gets `GetLog("Core.Charts")` and starts the scan on a
    worker; `StopAsync` cancels it, awaits it, and is idempotent;
  - the host may be null (the shared module contract test starts modules
    without one), or may not implement logging.
- `CoreCharts.csproj`: `StageDevelopmentCharts`, Debug only, after
  `DeployModule`, with two inline tasks. One extracts the zip, normalising
  backslash names and skipping unchanged entries. The other copies only
  changed files, keeping the source's last-write time so the next build
  can skip them.
- `README.md`: the inbox and the Debug staging.

One defect found and fixed during the run: the shared
`ModuleDeclarationTests.StopBeforeStart_IsSafeAndIdempotent` starts every
module with a null host, and the first version called `GetLog` on it. This
was a production fix; no test changed.

Results:
- CoreCharts.Tests: 205/205, including the local UKHO/SHOM tests on this
  machine.
- `dotnet build Poseidon.slnx`: 0 warnings, 0 errors.
- `dotnet test Poseidon.slnx -maxCpuCount:1`: every project passes,
  including the Civilian and Military aggregates (Core 141, CoreCharts
  205, Contracts 94, CoreTracks 74, CoreUI 55, CoreRadar 34, CoreAIS 30,
  CoreDisplay 19, CoreNavigation 8, CoreSonar 8, the CMS suites).
- A real Debug build left PowerUp, UKHO and the four SHOM sets (181 files)
  in `System/Core/Core/bin/Debug/net10.0/charts/inbox`.

**Step 3 gate (2026-11-01).** The SHOM staging paths exceeded Windows'
260-character limit. The user approved the flat layout and the change to
the approved test it needs:
- The staging task, `StageNumberedExchangeSets`, stages each set as
  `SHOM-FR-June26/NN-<set folder>/S100_ROOT` and removes any other folder
  there.
- `DebugStaging_CopiesTheLocalUkhoAndShomSets` now compares each numbered
  set with its source and checks that every staged path is at most 130
  characters below the inbox.
- The unused file-list helper, `Staging.ExchangeSetFiles`, was removed.

Rerun results:
- A real Debug build restaged the four SHOM sets as `01-`…`04-` and
  removed the old layout. Still 181 files; the deepest is now 217
  characters.
- CoreCharts.Tests: 205/205.
- `dotnet build Poseidon.slnx`: 0 warnings, 0 errors.
- `dotnet test Poseidon.slnx -maxCpuCount:1`: every project passes,
  including both aggregates.

Awaiting acceptance.

Follow-ups, outside this feature:
- Configurable store and inbox locations (per-user or per-machine data
  folder), carried from the pipeline feature.
- The operator load command and load report UI (M1-D).
- Zipped exchange sets in the inbox, if a delivery channel needs them.

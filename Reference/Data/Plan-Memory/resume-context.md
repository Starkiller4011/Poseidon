# Resume context

Updated: 2026-11-01 (M1-E fusion rework accepted and merged, PR #7/`c01fd22`; sensor-simulators deferred; M1-H code done and passing, awaiting acceptance; M1-I code done and passing, awaiting acceptance; M1-C Step 3 code done, at the Step 3 gate with three held test defects)

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
| 2. Tests | Done, approved by the user on 2026-11-01 | I1 to I12 all covered (18 new Core.Tests cases; I9 by regression guard, I12 by manual verification) |
| 3. Code | Done, presented for the gate | All 12 requirements implemented; `dotnet test Poseidon.slnx -maxCpuCount:1` fully green (every project "Test Run Successful"), 2026-11-01 |

Progress: `LoggingConfiguration.ResolveMinimumLevel`, `LogSink.Write`,
`ModuleRuntime.GetLog`, `Report` (now `GetLog("Core").Warning(...)`
instead of direct `Console.Error`) and `App.axaml.cs`'s two failure paths
are all implemented for real; `System/Core/Core/Properties/launchSettings.json`
adds the "Poseidon"/"Poseidon (Debug logging)" run profiles. One
deliberate, in-scope behavior note: `Report`'s diagnostics now go to
`Console.Out` via the shared sink instead of `Console.Error`, since the
facility writes every level to the same stream by design — not an
accidental regression. Full detail, including the requirement-to-test
ledger, is in the feature file's Step 3 entry, not repeated here. Gate:
waiting for the user's acceptance to close out M1-I.

A third feature is also in flight, by the user's explicit choice to start it
now rather than wait for M1-H/M1-I acceptance:
[M1-C: Chart-data pipeline](features/m1-c-chart-data-pipeline.md) (depends
only on M1-A, done). Charts reads real S-101 exchange sets (exchange-set
catalogue, a bounded ISO 8211 container reader, S-101 feature/geometry
decoding, base/update lineage, an immutable chart store) and exposes typed
query results; Display is not touched until M1-D.

| Step | State | Detail |
|---|---|---|
| 3. Code | Done; gate items approved 2026-11-01 | All of Slices 1-5 implemented; test defects D5-D7 corrected as approved; C30 baseline recorded; awaiting acceptance |
| 2. Tests | Approved 2026-11-01; three corrections proposed | All 37 requirements covered (165 new tests incl. 70 s100tds cases; 9 local-only); every new test fails with `NotImplementedException` except the C27 no-Charts regression guard, which already passes; ledger in the feature file. Gate items: proposed C17 wording change (older-edition update not flagged not-up-to-date) and a CI change to check out three submodules |
| 1. Plan | Done, approved 2026-11-01 (with its Step 2 amendment) | 37 requirements (C1-C37) across 5 slices; Q1-Q10 all answered; amendment: host-shared `IChartLibrary` contract in `System/Shared/Contracts/Charts.cs` for C27 (C27 reworded); revised 2026-11-01 after auditing the IHO documents and submodules against real bytes, raising Q6 (supported editions), Q7 (signature integrity) and Q8 (XML schemas, answered by the user's download) |

Progress: fixtures audited directly in the `S-101-Test-Datasets` submodule
(the `S-101_1.2` standalone base cell with its YAML sidecar as the decode
oracle; the smallest `S-158` case; the `S-164` Power-Up/Loading-of-Updates/
Invalid-Sequence/Re-issue/Cancellation/Corrupt-Update/Loading-Corrupt-Data
scenarios for lineage coverage). Chart store upgraded from in-memory to an
embedded database (assumed SQLite) with a cache kept warm for a
caller-named reference cell plus its catalogue-adjacent neighbors (the
user's explicit Q3 answer), adding C31-C33. Fetched and read the real
**S-100 Edition 5.1.0, Part 10a — ISO/IEC 8211 Encoding** (October 2023)
from IHO's public download — saved at
[Reference/Data/Specifications/](../Specifications/README.md) — confirming
the exact Leader/Directory/Field Area byte layout and the real S-100/S-101
record field tags (`DSID`/`DSSI`/`CSID`/`CRSH`/`PRID`/`MRID`/`CRID`/`CCID`/
`SRID`/`FRID`/`FOID`/`ATTR`/`INAS`/`SPAS`/`FASC`, the `NATC`/`ATIX`/`PAIX`
attribute-tree encoding, and the `RUIN`/record-order update rules), which
now grounds Slice 1 and most of Slice 2 in verified normative text instead
of the test corpus's non-normative YAML/XML oracles alone. Two narrower
gaps remain and are recorded as known, non-blocking scope: the S-101
Feature Catalogue's code-to-meaning mapping (likely IHO GI Registry; free
access unconfirmed) and the exchange-set-level dataset-lifecycle rules
(base/update/reissue/cancellation identity) — both already degrade
gracefully under this plan's existing diagnostics. Both gaps were then
closed by the user's IHO document download (S-101 PS and FC 2.0.0), and the
plan was revised after a full audit. Step 2 tests are written and fail for
the right reason (production code is compile-only stubs). Gate: waiting for
the user's approval of the tests, the proposed C17 wording change and the
CI submodule change, then the explicit order to continue to Step 3 (code).

[M1-E track fusion and queue ingress](features/m1-e-track-fusion.md)
(requirements R1 to R40 with R16a) is done: all 6 slices accepted, PR #7
merged into `dev` as `c01fd22` on 2026-11-01, with green cross-platform CI.

[Sensor simulators and ComputerGeneratedForce](features/sensor-simulators.md)
is plan-approved (2026-10-27) but deferred by the user's explicit choice on
2026-11-01: paused with no tests or code started, to be picked up later.

## Last changes made

M1-C Step 3 gate: the user approved the D5-D7 test corrections, the C30/C37
count corrections (27 UKHO cells; one truncated InitialPowerUp reference and
one UKHO typo reference, both reported, never resolved) and the managed
store in place of SQLite. Applied and rerun; results in the feature file.
Nothing committed.

Earlier in the same session, M1-C Step 3 finished: the user approved the `DS-NEWEDITION` correction and
asked that further test defects be held for the gate. Implemented the chart
library (`Store/`). It has a managed store of content-addressed files and an
atomically replaced manifest, immutable snapshots, a neighbourhood-pinned
LRU cache, ECDSA signature checks, coverage adjacency, and an import that
runs on a worker thread and can be cancelled, with support files. 171 of 174
CoreCharts tests pass. The 3 failures are held test defects, where the
real data differs from what the tests assumed:
- D5: support references are on information types, and one is truncated in
  the released data.
- D6: UKHO has 27 cells, not 29.
- D7: a UKHO cell names a support file with a digit missing.
The rest of the full solution is green. C30 baseline recorded. Nothing
committed.

Earlier in the same session, M1-C Step 3 continued after the user approved the corrected oracle tests:
implemented `S101UpdateApplier` (lineage rules, Part 10a record order,
attribute/association/spatial-list and positional edits), an S-101 Annex B
fallback for fields a file uses without defining (`ddr-definition-missing`
warning) and brace-delimited format controls in the ISO 8211 reader. 131 of
132 non-library CoreCharts tests pass, including all 70 s100tds cases; the
one failure is a test defect (`DS-NEWEDITION` is edition 3 in its bytes),
awaiting approval. Chart library not started. Nothing committed.

Earlier in the same session: the user approved the M1-C Step 2 tests, the C17 wording change and the CI
change, and said to continue. Applied the C17 wording; added a
`git submodule update --init --depth 1` step for the three test-data
submodules to the Windows and Linux jobs in `build-test-package.yml`
(`core.longpaths` on Windows). Started Step 3: implemented
`Iso8211Reader` (12/12 tests pass), `ExchangeSetReader` (17/17, C5 waits
for the library) and `S101DatasetDecoder` + internal `S101Fields` (17/20).
The 3 failures are a test defect: the tests assumed YAML sidecar record
names equal encoded RCNM+RCID, which the raw bytes disprove (e.g. YAML
`P11511` is record 115/1). Per the workflow, paused Step 3 and returned to
the Step 2 gate with proposed corrections (match oracle entries by
coordinates; drop the geometry-name check that C15 never required).
Also found: the host forbids native libraries in modules, so the store
will be managed (blobs + atomic manifest) instead of the assumed SQLite;
recorded for the Step 3 gate. Update applier and chart library not started.
Nothing committed.

Earlier in the same session: M1-C Step 2 (tests), after the user approved the C27 amendment: wrote the
compile-only production surface (`System/Shared/Contracts/Charts.cs`; in
`System/Core/CoreCharts`: `Iso8211/`, `ExchangeSets/`, `S101/`, `Lineage/`,
`Store/`, `ChartDiagnosticCodes.cs`; `module.json` 0.1.0 with the
`charts.library` point; `ChartsModule.Features`), and the tests in
`Tests/Core/CoreCharts.Tests` (`Iso8211ReaderTests`, `ExchangeSetReaderTests`,
`S101DatasetDecoderTests`, `S101UpdateApplierTests`, `ChartLibraryTests`,
`ChartsRuntimeTests`, `ReaderBoundaryTests`, `ChartBaselineTests`, plus
helpers `Fixtures`, `TestSupport`, `YamlOracle`); the test project now
references `Core`. Expected values were derived independently from the
data before writing tests (neighbour sets from catalogue polygons with
shapely, support-file references, feature types in the s100tds base, which
1.1.0 files carry INAS). `dotnet build Poseidon.slnx`: no warnings or
errors. `dotnet test Tests/Core/CoreCharts.Tests`: 174 tests, 165 failed
(all `NotImplementedException`, checked per test from the TRX), 9 passed
(8 pre-existing + the C27 no-Charts guard). `dotnet test Poseidon.slnx
-maxCpuCount:1`: every other project passes; only CoreCharts' 165 expected
failures, which the Civilian aggregate propagates as `MSB4181`. Ledger and
gate points are in the feature file. Nothing committed.

Earlier in the same session: the user approved the M1-C plan and ordered Step 2 (tests). Before writing
any test, read the workflow's Step 2 rules and the existing module/test
conventions (`IModule.Features`, `ModuleDeclarationTests`, the runtime's
`ModuleGraph`). Found that the runtime rejects any feature not
implementing a host-shared Contracts interface, so C27 could not be met
under the plan's "all new types inside CoreCharts" rule. Per the workflow
(a plan gap found while writing tests goes back to Step 1), amended the
plan's Design (a minimal host-shared `IChartLibrary` in
`System/Shared/Contracts/Charts.cs`; decoding/store internals stay in
CoreCharts) and reworded C27, and returned to the Step 1 gate. No tests
or production code written. Nothing committed.

Earlier in the same session: the user accepted Q10: added C37 (S-101 support files imported with their
datasets, signature-checked, stored with the snapshot, and resolved from
features' `fileReference`/`pictorialRepresentation`; missing files reported).
Confirmed committed data covers it for CI (the released 2.0.0
`InitialPowerUp` set has 5 support files referenced by cell `X0000`). All
M1-C open questions are answered; the plan is back at the Step 1 gate with
37 requirements. Nothing committed; no tests or code.

Earlier in the same session: before committing the plan, the user asked to explore the new UKHO data.
Extracted the S-102/S-104/S-111/S-124 zips in place (about 1.3 GB, all
git-ignored). Findings: every exchange set uses catalogue schema 5.2;
UKHO's S-104/S-111 sets also carry an S-128 dataset (mixed products);
SHOM's S-104/S-111 have placeholder signature text and no `CATALOG.SIGN`;
all other signatures verify (ECDSA/SHA-384). Inspected the HDF5 products
with `h5py` in a scratchpad virtualenv (Part 10c grids and time series,
with UKHO/SHOM attribute and field-name variants) and the S-124/S-128 GML.
For S-101: UKHO's set has 127 signed support files that 27 of 29 cells
reference via `fileReference`; SHOM's spreadsheet lists per-update changes
(usable as an oracle) but its feature counts are not exact (5,056 records
vs 5,027). Recorded in M1-C: C1 (other products listed as unsupported),
C36 (undecodable signature = unsigned), a corrected support-file count, and
new Q10 (support files in scope?). Recorded the non-S-101 product findings
and a new cross-cutting decision row (who reads S-102/104/111/124/128, and
how HDF5 is read) in the master plan. Nothing committed; no tests or code.

Earlier in the same session: the user added real hydrographic-office S-101 data at
`Reference/Data/S-101/S-101/`: `S-101_GB_Apr26.zip` (UKHO, 29 cells, one
update, 254 support files) and `S-101_FR_June26.zip` (SHOM, four exchange
sets incl. cell `101FR00369400` across editions 1 and 2). Extracted both
(and their nested zips) in place, keeping the zips. All 35 files are S-101
2.0 / Part 10a 5.2, structurally clean, with matching `DSSI` counts and
mandated coordinate factors; all 36 signatures verify (ECDSA/SHA-384, IHO
5.2.0 root SA). New variants recorded in M1-C: base `DSED` as `"N.0"`,
UKHO's `S100_SE_DigitalSignature` element, SHOM's per-cell subfolders.
Wired the data into M1-C's fixture table and C16, C18, C19, C30, C36. The
user confirmed it came from UKHO and must never be committed: added
root-anchored entries for `Reference/Data/S-101`, `S-102`, `S-104`,
`S-111` and `S-124` (all UKHO downloads, about 690 MB, none previously
tracked) to the root `.gitignore`, verified with `git check-ignore`, and
recorded in M1-C that tests using this data skip when it is absent while
committed data still covers every requirement (C30's primary baseline
moved back to the committed `PowerUp` set). Nothing committed; no tests or
code.

Earlier in the same session: the user accepted Q9: added `github.com/kusala9/s100tds` as submodule
`Reference/Data/s100tds` (pinned at `4c2cade`) and recorded it in M1-C's
fixture table (update-mechanics cases, read through a test-only override
applying the 2.0.0 rules to its 2.1.0 label; its 2.10.0 cases are not
used). All M1-C open questions (Q1-Q9) are now answered; the plan is back at
the Step 1 gate. Asking the `s100tds` author for a licence and a 2.0.0
build, and the S-164 sub-group about 2.0.0 lifecycle sets, is the user's to
do. Nothing committed; no tests or code.

Earlier in the same session: the user accepted the recommendations for M1-C's Q6 (decode S-101 2.0.0
fully, accept 1.x at the record level) and Q7 (signature integrity against
the catalogue's own certificate, no trust chain), recorded in the plan with
a new requirement C36. The user then asked to look for real S-101 2.0.0
test data. Searched the S-164 sub-group's staging area (same `X01SW`
lineage with all five invalid-sequence files, plus 46 more 2.0.0 base
cells) and its GitHub `v2.0.0` release (2026-10-16; same 39 sets as the
repo, more promised), the IHO GI Registry (no 2.0.0 test dataset), UKHO
(free trial data behind the ADMIRALTY portal licence, edition unstated) and
CHS (sold through PRIMAR). No 2.0.0 re-issue, cancellation or corrupt
update exists anywhere. Found the unofficial, unlicensed
`github.com/kusala9/s100tds` (created 2026-10-26): about 80 update classes
covering every Annex B-6 update mechanism; cloned it to the scratchpad and
parsed all 444 files cleanly; its files are labelled S-101 2.1.0 (at IHO
call for approval, draft FC) but every code they use exists in FC 2.0.0.
Raised Q9 on whether and how to use it. Nothing committed; no submodule
added; no tests or code.

Earlier in the same session: the user added `Reference/Data/distribution_20240521/`, the S-100 schema
server's 2024-05-21 archive (5.0.0/5.1.0/5.2.0 schemas plus mirrored ISO
TC 211 and OGC schemas), answering M1-C's Q8. Inventoried it (it covers all
three exchange-catalogue namespaces the data uses; no sample catalogues)
and validated every exchange catalogue in the corpora plus the S-101
Feature Catalogue 2.0.0 fully offline, using a throwaway .NET 10
file-based validator in the session scratchpad with a resolver mapping the
schema hosts and W3C URLs onto the archive. FC 2.0.0 is valid; 80 of 118
catalogues are not, including every catalogue in M1-C's fixtures, which
confirms the plan's tolerant-catalogue decision (finding 3). Recorded
provenance and results in the Specifications README, marked Q8 answered in
the M1-C plan, and pointed the master plan at the archive. Q6-Q7 remain
open. Nothing committed; no tests or code.

Earlier in the same session: audited all the documentation and submodules against
[M1-C's plan](features/m1-c-chart-data-pipeline.md) to see whether
everything needed to implement it is present. Read the S-101 2.0.0 Product
Specification (including normative Annex B), the relevant parts of S-100
5.2.1 (10a, 15, 17), the S-164 Ed 1.0.0 manual's loading/update tests, and
the S-101 2.0.0 Feature Catalogue; surveyed every submodule; and parsed
every S-101 file in the corpora (about 600) with throwaway Python ISO 8211
tools kept in the session scratchpad, not the repo. Verdict: the normative
material is complete (both earlier gaps closed); missing are 2.0.0
re-issue/cancellation/corrupt-update fixtures (none exist anywhere) and the
S-100 XML schemas (public at `schemas.s100dev.net`, not in the repo). Key
findings now in the plan's Design section: the reader must be DDR-driven
because 1.x and 2.0.0 encode repeating groups and text differently; the
catalogue is unreliable (the released 2.0.0 updates are all labelled
`newDataset`), so the file's `DSID` is authoritative (new C35); adjacency
must be computed from coverage polygons (C32); `DSSI` counts are wrong in
about half the files; the old "corrupt" fixtures are not corrupt (byte-
identical files, valid signatures), so C18/C21 were re-pointed; signatures
verify offline (ECDSA/SHA-384) against the catalogue's embedded
certificate. Moved the fixture set to the released S-101 2.0.0 S-164 Part 2
exchange sets (`S-164-Sub-Group`), rewrote C1, C6, C16-C21, C30, C32, added
C34-C35, and raised Q6 (supported editions), Q7 (signature integrity in
scope?) and Q8 (pin the XML schemas?). Updated the master plan and the
Specifications README (its Part 10a 5.1.0 copy is superseded by S-100
5.2.1). Nothing committed; no tests or code.

Earlier in the same session: the user added a bulk download of official IHO documents at
`Reference/Data/S-100 Documentation/` (specs, feature/portrayal catalogues,
S-158 validation-check documents, S-130 sample data) and asked for its zip
files extracted. Extracted every zip (including portrayal-catalogue zips
nested inside the signature-wrapper zips) into a same-named sibling folder;
left the original zips in place rather than deleting them (deleting
project files is outside auto-mode's allowed actions). The user then asked
to add a number of the `iho-ohi` GitHub org's repositories as submodules
under `Reference/Data`, the same pattern already used for
`S-101-Test-Datasets`. Peeked at several candidates' top-level contents to
tell real data/code repos from empty or working-group discussion repos,
then confirmed a shortlist with the user (plus one they added,
`S-100-Standard-Development`) before adding anything; the user then named
8 more by exact URL directly. Added 13 new submodules in total:
`S-100-Standard-Development`, `S-100-xmlbindings`, `S-101-Validation`,
`S-100-Validation-Checks`, `S-101_Portrayal-Catalogue`, `S-164-Sub-Group`,
`NIWCSoftwareTestbed`, `S100Infrastructure`, `S100Resources`, `S-100UML`,
`S-100WG`, `ENCWG`, `S100-Security-Scheme-PT`. One
(`S-101-Validation`) has two upstream folder names containing a literal
`:` (`Published versions/S-158:101 edition 1.0.0` and `.../1.1.0`), which
Windows/NTFS cannot check out; `core.protectNTFS=false` is a blocked
auto-mode action, so instead materialized every other tracked file
directly from the `main` tree via `git show` and built a matching partial
index, leaving just those two folders absent (their content duplicates the
official S-158-101 docs already under `S-100 Documentation/`). `git status`
inside that one submodule will always show those 5 files as deleted; this
is a known, accepted gap, not an error to fix. Nothing committed.

Closed out all 5 open questions on
[M1-C: Chart-data pipeline](features/m1-c-chart-data-pipeline.md)'s Step 1
plan. The user's answers: Q1 ISO 8211 in-house (confirmed the assumption);
Q2 only test data was available, which Q4 then superseded; Q3 an embedded
database with a cache kept warm for the currently-displayed cell plus its
adjacent cells, not the in-memory-only default — added requirements
C31-C33 and rewrote the chart-store Design section and a non-goals bullet
accordingly; Q5 CRS scope confirmed. Q4 asked to build against the real
S-100 specification, not just the test-corpus shape: used a `fork` to
confirm IHO publishes S-100/S-101 publicly (search only, no page-fetching
tool available to it), then a `web-fetch` agent to pull the S-100 Part 10a
PDF, which returned raw undecoded bytes; extracted the real text myself
with `pdftotext -layout` (available at `/mingw64/bin/pdftotext`) and read
all 44 pages directly. Saved the PDF and extracted text at
[Reference/Data/Specifications/](../Specifications/README.md) with a
README recording provenance, edition, and what the document does and does
not cover, so a future session does not have to re-research this. Folded
the verified field tags, record structure, and update mechanism into the
plan's Design section, replacing the old "specification gate" uncertainty;
flagged the two narrower remaining gaps (Feature Catalogue code meanings;
exchange-set-level lifecycle rules) as known non-blocking scope. Updated
the master plan's M1-C rows and this file. Nothing committed; no tests or
code started. Presented in conversation, waiting for the user's explicit
order to continue to Step 2 (tests).

Earlier in the same session: drafted the initial Step 1 plan for M1-C
(before the open questions above were answered): audited the
`S-101-Test-Datasets` submodule directly (fixture sizes, the S-164
scenario-folder names) rather than relying only on the existing
Feature-Mappings documents, and wrote the original 30 requirements
(C1-C30) across 5 slices.

Earlier in the same session: M1-I Step 3 (code): implemented all 12 requirements (I1 to I12) for real
on branch `feature/core-track-system`, not committed yet.
`LoggingConfiguration.ResolveMinimumLevel` checks `POSEIDON_LOG_LEVEL`
first, then `logging.json` (read from `AppContext.BaseDirectory`), then
falls back to `Information`; `LogSink.Write` filters by level, formats
timestamp/category/level/message(+exception), writes to console then a
file, and swallows file-write failures. `ModuleRuntime` builds one shared
`LogSink` at construction (log file at `logDirectory/poseidon-<date>.log`,
`logDirectory` defaulting to a `logs` folder next to the host executable)
and implements `GetLog` for real; `Report` now calls
`GetLog("Core").Warning(...)` instead of `Console.Error.WriteLine`
directly, and `App.axaml.cs`'s two failure paths now call
`GetLog("Core").Error(...)` the same way (verified by reading the diff;
no test harness exists for the `App` class). New
`System/Core/Core/Properties/launchSettings.json` with "Poseidon" and
"Poseidon (Debug logging)" profiles. `dotnet build Poseidon.slnx` and
`dotnet build Core.Application.proj`: no warnings or errors. All 18 new
`Core.Tests` cases now pass; the full `Core.Tests` project is 141/141;
`dotnet test Poseidon.slnx -maxCpuCount:1`: every project in the solution
reports "Test Run Successful", zero failures anywhere.

Earlier in the same session: M1-I Step 2 (tests): wrote and ran the tests for all 12 requirements
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
2. **Get the user's acceptance of [M1-I's code](features/m1-i-module-logging.md)**
   to close out the milestone; the full passing run is recorded and
   waiting for review. Nothing is committed yet.
3. **Get the user's acceptance of [M1-C's code](features/m1-c-chart-data-pipeline.md)**
   to close out the milestone. The Step 3 gate items (held test defects
   D5-D7, count corrections, managed store) are approved and applied.
   Nothing is committed yet.

Everything else waiting, unblocked by anything above and not in
competition with it, per the master plan's milestone table:

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

M1-C now has a revised plan awaiting the order to continue (above); M1-F has no plan yet
and no dependency on M1-C, so it can be planned next whenever the user wants
a fourth feature in flight. M1-D and M1-G both wait on earlier work (M1-G
also now waits on M1-H).

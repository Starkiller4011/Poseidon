# Poseidon master plan

Date: 2026-10-26 (restructured from the single application plan of 2026-10-24)

This is the top-level plan. It holds the purpose, the confirmed architecture
decisions, ownership and data flow, the milestones and the cross-cutting
decisions that are still open. Everything specific to one feature lives in its
own file under `features/`.

Status: M1-A (host foundation) is complete for its automated cross-platform
gate. M1-B (CoreUI/Display viewport boundary) is merged and passed
cross-platform CI. M1-E (track pipeline) is implemented and verified on Windows
as single-source tracks; its acceptance is on hold because the fusion
requirements confirmed on 2026-10-26 replace parts of it. That rework's slice 1
(contracts) and slice 2 (estimator) are both accepted; slice 3 (manager) has
not started. This is not a certification claim.

## How this documentation is organized

All files are in `Reference/Data/Plan-Memory/`.

| File | Role |
|---|---|
| [development-workflow.md](development-workflow.md) | How every feature is developed: plan, then tests, then code, with a gate after each step. This is also the test-first rule. Read it first |
| [resume-context.md](resume-context.md) | How to resume work: the primary resume context, the last changes made and what needs to be done next. Read it at the start of every session |
| This file | Purpose, architecture, milestones, open cross-cutting decisions |
| [Feature-Mappings/](Feature-Mappings/README.md) | The data format schema mappings of the input data used for chart data (S-101, S-158 and S-164). Reference material for the chart features, not a plan |
| `features/*.md` | One file per feature: plan, requirements, decisions, ledger and status |

### Features

| Feature file | Milestone | Status |
|---|---|---|
| [m1-a-host-and-module-loading.md](features/m1-a-host-and-module-loading.md) | M1-A | Done for the automated gate; native Linux desktop smoke tests deferred to M1-G |
| [m1-b-ui-and-viewport.md](features/m1-b-ui-and-viewport.md) | M1-B | Done; merged after cross-platform CI |
| [m1-c-chart-data-pipeline.md](features/m1-c-chart-data-pipeline.md) | M1-C | Not started; no plan approved |
| [m1-e-track-pipeline.md](features/m1-e-track-pipeline.md) | M1-E (as built) | Implemented and verified on Windows; acceptance on hold pending the rework |
| [m1-e-track-fusion.md](features/m1-e-track-fusion.md) | M1-E (rework) | Plan approved 2026-10-26 (bearing tracks in Core added); slice 1 (contracts) merged; slice 2 (estimator) accepted; slice 3 (manager) not started |
| [cms-backlog.md](features/cms-backlog.md) | CMS | Documentation only; deferred until the civilian side is complete |

M1-D, M1-F and M1-G get their feature files when their planning starts.

### Section map

Material from the former single plan (`2026-10-24-poseidon-application-plan.md`)
kept its section numbers inside the feature files, so older references stay
traceable.

| Former section | Now in |
|---|---|
| 1 Resume context | Purpose and architecture decisions below; the orientation in [resume-context.md](resume-context.md); the test-first rule in [development-workflow.md](development-workflow.md); reference contracts in [Feature-Mappings](Feature-Mappings/README.md) (see "Reference data mappings" below) |
| 2 Pre-implementation baseline | [M1-A](features/m1-a-host-and-module-loading.md) |
| 3 Requirements already established | Tests below; modules and deployment in [M1-A](features/m1-a-host-and-module-loading.md); chart presentation in [M1-C](features/m1-c-chart-data-pipeline.md); consoles and activities in [CMS backlog](features/cms-backlog.md) |
| 4 Architecture and ownership | Ownership and data flow below; sensor-to-track proposal in [M1-E](features/m1-e-track-pipeline.md); deployment, discovery, hot reload, overrides, platforms and module contract in [M1-A](features/m1-a-host-and-module-loading.md) |
| 5 Chart ingestion and storage contracts | [M1-C](features/m1-c-chart-data-pipeline.md) |
| 6 UI and console interaction | UI in [M1-B](features/m1-b-ui-and-viewport.md); multi-console coordination in [CMS backlog](features/cms-backlog.md) |
| 7 Milestones | Below |
| 8 Verification strategy | General items below; loader items in [M1-A](features/m1-a-host-and-module-loading.md); chart items in [M1-C](features/m1-c-chart-data-pipeline.md) |
| 9 Decisions still requiring confirmation | Below, with chart and CMS rows in their feature files |
| 10 Next implementation session | Replaced by [resume-context.md](resume-context.md) |
| 11 to 16 Host foundation, tests, splash, discovery, extension points, naming | [M1-A](features/m1-a-host-and-module-loading.md) |
| 17 Requirement-to-test ledger | Split by feature: M1-A, M1-B and M1-E rows in their files |
| 18 M1-B | [M1-B](features/m1-b-ui-and-viewport.md) |
| 19 M1-E track pipeline | [M1-E as built](features/m1-e-track-pipeline.md) |
| 20 and 21 Track requirements and rework plan | [M1-E rework](features/m1-e-track-fusion.md) |

## Purpose

Primary objective: a modular maritime desktop application that reads actual
S-100-series chart data, initially S-101, and supports civilian navigation and
role-based multi-console operation without coupling chart decoding to the UI.

The first milestone is **the initial core application**: Core, CoreUI, Display,
Charts, Navigation, Tracks, AIS, Radar, and Sonar. Deliver it incrementally,
starting with the reloadable host and UI, then real chart reading/display, then
track ingestion/display and navigation calculations. CMS functionality and
distributed role/activity coordination are long-term backlog work, not
prerequisites or parallel deliverables for this milestone. M1 is a development
core application, not certified ECDIS.

## Confirmed architecture decisions (2026-10-24)

The user explicitly confirmed the following boundaries after the initial plan:

- [System/Core/Core](../../../System/Core/Core/Core.csproj) is the application
  core and main executable. Application startup, lifetime, composition, and
  runtime module loading belong here. CoreUI owns the application UI.
- The other runtime projects under `System/Core` and `System/CMS` build module
  libraries. Their binaries are deployed into the application's `modules`
  directory and add functionality through runtime discovery, not a static
  feature list compiled into the executable.
- [System/Core/CoreCharts](../../../System/Core/CoreCharts/CoreCharts.csproj) owns reading
  chart data. Core must not implement an alternative chart reader.
- Reference tooling, test projects, and any shared-contract library are not
  feature modules and must not be scanned or deployed as such.
- Hot reload is mandatory for development and end-user troubleshooting: module
  additions, replacements, and removals must be handled without restarting Core.
- Modules can explicitly override features supplied by other modules, enabling
  mod-like extensions and hot patches.
- Windows, Arch Linux, and RHEL are the supported target platforms from the
  outset. macOS is out of scope.
- CoreUI owns UI code; Display owns chart-viewport drawing and feature layers;
  Charts reads chart data and exposes it to Display.
- Tracks owns track type definitions and the authoritative track manager.
  AIS and Radar receive source data and request track creation/updates
  through Tracks; Display draws the resulting managed tracks. Civilian Core
  Sonar (echo sounder/depth, bathymetry) is not a track source and never
  submits to Tracks (user-confirmed 2026-10-26).
- Navigation owns navigation functions, including CPA calculations.
- The initial core application is the first milestone. CMS is deferred until
  much later; Core Sonar is distinct from any future CMS sonar functionality.

These decisions supersede any conflicting provisional ownership statements in
the feature files. Track-system decisions confirmed on 2026-10-26 (fusion in
CoreTracks for both products, CmsTracks expanding it, per-source timings,
sensors publishing to queues) are in
[m1-e-track-fusion.md](features/m1-e-track-fusion.md).

## Reference data mappings

The data format schema mappings of the input data used for chart data live in
[Feature-Mappings/](Feature-Mappings/README.md), beside the plans:

- [S-101 chart data and reader contract](Feature-Mappings/s-101-data-contract.md)
- [S-158 validation-test contract](Feature-Mappings/s-158-data-contract.md)
- [S-164 scenario contract](Feature-Mappings/s-164-data-contract.md)
- [Dataset dependency declaration](../../../.gitmodules)

Dataset revision inspected during mapping:
`6fbc77360695ba75db9b460b604cb865caaaa8f4` of
`https://github.com/iho-ohi/S-101-Test-Datasets.git`.

These mappings are preliminary contracts, not complete normative schemas.
S-158/S-164 in this corpus wrap or exercise S-101 payloads; they are not two
additional chart decoders. Do not claim support for all S-100 products from
support for these three directories. The M1-C feature
([m1-c-chart-data-pipeline.md](features/m1-c-chart-data-pipeline.md)) builds on
them.

## Established requirements

### Tests

- Keep one independent test project per module.
- Keep civilian and military aggregate entry points separate.
- Run the actual applicable module suites through each aggregate entry point,
  not only shared smoke functions.

Module and deployment requirements are in
[m1-a-host-and-module-loading.md](features/m1-a-host-and-module-loading.md).

## Architecture and ownership

The application-core/module split and Charts reader ownership are confirmed.
Use an in-process modular desktop application initially as the proposed loading
model. Avoid service proliferation; add a network boundary only where
cross-console coordination requires one.

```text
System/Core/Core: main executable and Avalonia application bootstrap
  -> runtime discovery from <application-directory>/modules
  -> shared module/activity contracts
  -> discovered module registrations
       -> CoreUI: windows, panels, commands, diagnostics presentation
       -> Charts: exchange reader -> ISO 8211 -> S-101 -> chart store
       -> Display: chart viewport, feature layers, track overlays
       -> Navigation: own ship, routes, CPA/TCPA
       -> AIS / Radar: input adapters and source observations
       -> Sonar: depth readings and bathymetry (not a track source)
       -> Tracks: track types, authoritative track state and change feed

Future CMS modules: outside the initial core-application milestone

Reference/Data tooling -> chart public reader APIs, never the reverse
UI -> module services; chart decoding/domain models -> no Avalonia dependency
```

| Existing project (short names; module IDs and directories are in the M1-A file, section 16) | Intended responsibility |
|---|---|
| Core | Main executable, application lifetime, runtime module loading/hot reload, feature registry, composition, diagnostic events |
| CoreUI | UI module: windows, shell/workspaces, panels, commands and diagnostics presentation; hosts Display's viewport |
| Display | Drawing module: chart viewport, projection/view transforms, feature layers, track symbols/overlays and hit testing |
| Charts | Data module: exchange discovery, version dispatch, S-101 reading, update lineage, immutable chart data and queries; no drawing |
| Navigation | Own-ship/navigation state, routes, CPA/TCPA and other navigation calculations; no drawing |
| AIS | AIS ingestion, decoding, source health, observations |
| Tracks | Track type definitions, creation/update/lifecycle, identity, source provenance, freshness, snapshots and change feed |
| Radar | Receive/normalize target reports and submit observations to Tracks; raw video processing requires separate scope |
| Sonar | Civilian echo-sounder depth readings and bathymetry; not a track source and does not depend on Tracks |
| CMS projects | Deferred extensions, including future role/activity coordination and specialized UI/tracks/sensor behavior |
| Reference/Data | Dataset inventory/inspection and fixture comparison tooling |

Route ownership needs deliberate reconciliation: Charts currently advertises
`charts.route-planning`. Proposed split: Display provides map interaction,
Charts provides chart queries, and Navigation owns routes and navigation
calculations. Migrate
the capability deliberately, preserving compatibility where required.

Add a UI-independent contracts library only when resolving the current missing
shared types and plugin-to-executable reference problem. Give it its own test
project and include it in both aggregate suites. Keep S-101 internals inside
Charts initially rather than creating multiple speculative libraries.

### Runtime dependencies and data flow

The following dependencies are on versioned contracts/providers, not references
to another plugin's implementation assembly:

| Consumer | Required provider | Optional integration |
|---|---|---|
| Core | Shared host contracts only | Discovered modules; no concrete module reference |
| CoreUI | Host UI/lifecycle contracts and shared Avalonia runtime | Display viewport and module-contributed panels |
| Charts | Host contracts | No Display, Tracks, sensor, Navigation or CMS requirement |
| Tracks | Host and track contracts | No sensor, Display, Charts or CMS requirement |
| Display | Host and render-data contracts | Charts for chart layers; Tracks for track overlays; Navigation for annotations |
| Navigation | Host and navigation contracts | Tracks for target CPA/TCPA; Charts for chart-aware functions |
| AIS, Radar | Host contracts and Tracks ingestion provider | Display is not required |
| Sonar | Host contracts | No Tracks requirement; depth and bathymetry consumers are separate scope |

Display may provide an empty viewport without chart/track providers. A feature
with a missing optional provider is explicitly unavailable, not silently
successful. Runtime dependencies must describe the required feature, not force
unrelated features of a whole module to be present. A sensor module without
Tracks is blocked with a clear dependency diagnostic.

CoreUI and Display must not depend on each other: CoreUI consumes a viewport
contribution contract; Display supplies it and consumes only host UI services.
Core retains the Avalonia application bootstrap, but application windows,
view models, styles and normal diagnostics UI move to CoreUI. Without CoreUI,
Core remains running with host logging and a minimal recovery/status mechanism;
that exceptional mechanism is not a second full UI implementation.

```text
Chart files -> Charts -> chart snapshots/queries -> Display -> viewport
AIS / Radar -> observations -> Tracks -> track snapshots/change feed
                                              -> Display track layer
                                              -> Navigation CPA/TCPA
Own-ship state -------------------------------> Navigation
Navigation annotations -----------------------> Display
CoreUI -> hosts viewport and binds module commands/panels
Core -> discovers, composes and reloads providers for all of the above
```

Tracks defines the public track/observation vocabulary, but publish those
definitions through a small host-shared track-contract assembly, not the
collectible Tracks implementation. Apply the same split to chart/render and
navigation boundary types as needed. Semantic ownership stays with the module;
loading a second copy of its contract types into each plugin would break type
identity and hot reload. Contracts are libraries, not discovered modules.

## Milestones and acceptance gates

**M1 is the initial core application**, delivered through the work packages
below. These supersede the earlier M0-M8 numbering. Do not implement CMS while
working on M1. Each work package is a feature (or several) developed under
[development-workflow.md](development-workflow.md).

| Work package | Prerequisite | Deliverables and exit evidence | Status |
|---|---|---|---|
| M1-A: Host and contracts | None | Restore shared types; keep Core as executable and modules as libraries; hot reload/overrides and repeatable packaging; no-module startup; replacement/removal/rollback tests on Windows, Arch Linux, and RHEL; independent core-module tests | Done for the automated gate |
| M1-B: UI and viewport | M1-A | CoreUI owns normal UI; Display contributes empty viewport and layer API; UI/Display reload without restarting Core; diagnostics/recovery remain available | Done |
| M1-C: Chart-data pipeline | M1-A | Audit specifications/resources, pin fixture versions; Charts decodes a real base cell; verify metadata, attributes, topology and malformed-input diagnostics | Not started |
| M1-D: Integrated chart view | M1-B, M1-C | Display draws Charts data; pan/zoom, locked base, overlays, contour slider and feature inspection; demonstrate Charts/Display provider replacement | Not started |
| M1-E: Track pipeline | M1-A; UI integration needs M1-B | Tracks contracts/manager plus AIS/Radar adapter boundaries (Sonar is not a track source); recorded/simulated observations produce managed tracks and Display updates; lifecycle, resync and reload tests; fusion, per-source timings and queue ingress (rework) | Single-source version built; fusion rework planned |
| M1-F: Navigation functions | M1-E | Own-ship state and tested CPA/TCPA; Display annotations; unavailable/stale/degenerate-motion behavior is explicit; a fault when own-ship position or heading is unavailable | Not started |
| M1-G: Integration gate | M1-D, M1-E, M1-F | Windows/Arch Linux/RHEL publish and native desktop smoke tests (RHEL with SELinux enforcing); actual civilian module suites; dependency, override and hot-reload failure tests across the integrated application | Not started |

M1-C and M1-E can proceed independently after M1-A. The M1-G gate requires no
CMS binaries. Device-specific protocols and hardware integrations are selected
separately; initial simulated adapters do not imply production device support.
M1 is a development core application, not certified ECDIS.

Follow-on core work includes full chart installation/update management,
expanded portrayal and real sensor protocols. Preserve the update-validation
requirements in the M1-C file; agree their release scope after the first
integrated slice. CMS roles, multi-console coordination and specialized modules
remain long-term backlog and do not block core development.

## Verification strategy (general)

- Module tests: pure domain/parser tests plus focused integration tests. Add UI
  interaction tests only for behaviors requiring the actual view layer.
- Aggregate execution: retain the two aggregate projects, add a deterministic
  orchestration mechanism that executes each selected module test assembly,
  propagates any failure, and reports test counts/results. A project reference
  or one aggregate smoke fact is not complete suite execution.
- Civilian membership: Core/contracts, CoreUI, Display, Charts, Navigation, AIS,
  Tracks, Radar and Core Sonar tests. Add missing independent test projects.
  Test the sensor-to-Tracks-to-Display flow, snapshot/feed continuity, source
  identity reuse and reload preservation, plus numerical CPA/TCPA edge cases.
  Military aggregate includes civilian
  coverage and applicable CMS modules.

Loader, hot-reload and override verification is in
[m1-a-host-and-module-loading.md](features/m1-a-host-and-module-loading.md);
chart verification in [m1-c-chart-data-pipeline.md](features/m1-c-chart-data-pipeline.md).

## Decisions still requiring confirmation

These do not block saving the plan, but must be resolved before their dependent
milestones:

| Decision | Provisional position | Resolve before |
|---|---|---|
| Release scope | Initial core application confirmed; agree depth of device integration and follow-on chart update support | M1-G scope review |
| Platform details | Windows, Arch Linux, and RHEL confirmed; macOS excluded. Select Linux windowing environments, architectures and hardware | M1-A test/publish matrix |
| Inputs/protocols | Recorded and simulated observations before live devices; select AIS/Radar device protocols separately (Sonar is depth/bathymetry, outside the track pipeline) | M1-E and later device integration |
| Route/navigation behavior | Navigation owns route state/calculations; Display owns map interaction; Charts supplies chart data | M1-F and later route tools |

Chart-related decisions (product and specification versions, trust and
licensing, portrayal and certification) are in
[m1-c-chart-data-pipeline.md](features/m1-c-chart-data-pipeline.md); CMS decisions
(console topology, CMS scope, the Core/CMS track relationship) are in
[cms-backlog.md](features/cms-backlog.md).

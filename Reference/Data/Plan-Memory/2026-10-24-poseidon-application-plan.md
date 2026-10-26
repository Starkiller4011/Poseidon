# Poseidon Application Plan

Date: 2026-10-24  
Status: Confirmed application/module boundaries with a proposed implementation
roadmap; host foundation implementation started. See section 11 for verified
progress and remaining work; this is not a certification claim.

## 1. Resume context

Read this file first when resuming application planning, then inspect the current
source before applying it. This plan reflects the repository on the date above;
update decisions and milestone status here as implementation progresses.

Primary objective: a modular maritime desktop application that reads actual
S-100-series chart data, initially S-101, and supports civilian navigation and
role-based multi-console operation without coupling chart decoding to the UI.

The first milestone is now confirmed as **the initial core application**:
Core, CoreUI, Display, Charts, Navigation, Tracks, AIS, Radar, and Sonar.
Deliver it incrementally, starting with the reloadable host and UI, then real
chart reading/display, then track ingestion/display and navigation calculations.
CMS functionality and distributed role/activity coordination are long-term
backlog work, not prerequisites or parallel deliverables for this milestone.

### Confirmed architecture decisions (2026-10-24)

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
- Windows and Linux are supported target platforms from the outset. macOS is
  out of scope.
- CoreUI owns UI code; Display owns chart-viewport drawing and feature layers;
  Charts reads chart data and exposes it to Display.
- Tracks owns track type definitions and the authoritative track manager.
  AIS, Radar, and Sonar receive source data and request track creation/updates
  through Tracks; Display draws the resulting managed tracks.
- Navigation owns navigation functions, including CPA calculations.
- The initial core application is the first milestone. CMS is deferred until
  much later; Core Sonar is distinct from any future CMS sonar functionality.

These decisions supersede any conflicting provisional ownership statements
below. The loader mechanics and override protocol below are proposed designs
for meeting these confirmed requirements, not implemented behavior.

### Existing reference contracts

- [S-101 chart contract](../Feature-Mappings/s-101-data-contract.md)
- [S-158 validation-test contract](../Feature-Mappings/s-158-data-contract.md)
- [S-164 scenario contract](../Feature-Mappings/s-164-data-contract.md)
- [Dataset dependency declaration](../../../.gitmodules)

Dataset revision inspected during mapping:
`6fbc77360695ba75db9b460b604cb865caaaa8f4` of
`https://github.com/iho-ohi/S-101-Test-Datasets.git`.

These mappings are preliminary contracts, not complete normative schemas.
S-158/S-164 in this corpus wrap or exercise S-101 payloads; they are not two
additional chart decoders. Do not claim support for all S-100 products from
support for these three directories.

## 2. Pre-implementation baseline

This section records the starting state, not the current implementation status.
See section 11 for changes made during implementation.

The [solution](../../../Poseidon.slnx), re-inspected after restructuring, contains
33 projects: 18 application/module projects, 12 existing module test projects,
two aggregate test projects, and one reference-data console project.
CoreUI, Display, and Core Sonar now exist; corresponding independent test
projects are still needed. CMS projects have been renamed/expanded; their
test-project alignment can be addressed when CMS work resumes.

| Area | Observed state | Planning consequence |
|---|---|---|
| Runtime | Projects target .NET 10; desktop host uses Avalonia 11.3.7 | Keep this stack initially; establish a reproducible SDK/build baseline |
| Desktop host | [Core](../../../System/Core/Core/Core.csproj) is a WinExe with a module-loader status window | Retain application bootstrap in Core; move normal UI to CoreUI |
| Startup | [App](../../../System/Core/Core/App.axaml.cs) calls `FileDropModuleLoader` | Preserve file-drop discovery as the intended deployment model |
| Modules | Module classes declare IDs, versions, and capabilities | Functional services, UI contributions, and lifecycle remain to be designed |
| Shared types | Source references `Poseidon.Core.Modules` and `Poseidon.Domain.Modules`, but their defining source files were not present in the inspected tree | Audit and restore/implement contracts before assuming the solution builds |
| Dependency direction | Older module projects reference the Core executable; newer scaffolds have no such reference | Establish shared contracts instead of implementation/host references |
| New module output | CoreUI, Display, and Core Sonar currently declare executable output | Convert these to module libraries during foundation implementation |
| Chart data | Submodule and preliminary mapping documents exist | Build an actual ISO 8211/S-101 pipeline; do not substitute YAML fixtures |
| Reference tool | [Data](../Data.csproj) is a console scaffold with a Hello World entry point | Reserve for inventory, inspection, and fixture tooling, not a production dependency |
| Tests | xUnit projects contain module-ID smoke checks | Add behavioral tests alongside existing checks |
| Aggregates | Aggregate facts explicitly call static smoke helpers | Referencing test assemblies does not execute their complete suites |
| Civilian suite | Currently references Core, Charts, AIS, and Tracks tests, but not Navigation or Radar | Reconcile civilian suite membership with intended civilian functionality |
| Dataset project item | Core includes a relative dataset glob pointing outside the current reference-data location | Correct/remove it during implementation; do not bundle the entire corpus into runtime output |

No build or test run was performed for this documentation task. The missing
source and project-wiring observations are prerequisites to investigate, not
newly verified compiler failures.

## 3. Requirements already established

### Modules and deployment

- Deliver optional modules by file drop. If a module is deployed, its
  capabilities are available at every console subject to activity ownership
  and locks; do not add per-console module enable/disable configuration.
- Discover modules at startup and reconcile runtime additions, replacements,
  and removals. Hot reload is a foundation requirement, not a later enhancement.
- Allow explicit, deterministic feature overrides through shared contracts.
  Overriding a feature does not require duplicating another module's identity.
- Report missing dependencies, incompatible contract versions, duplicate IDs,
  and initialization failures explicitly.
- A failed optional module must not silently disable functionality or hide its
  error. State which capabilities are unavailable and why.
- Module availability and permission to execute an activity are separate.

### Chart presentation

- Land and coastline form a non-toggleable base layer.
- Other thematic content is stacked in toggleable overlays.
- Bathymetric depth contours have a dedicated layer and coarse-to-fine interval
  slider.
- The interval control filters existing encoded contours; it does not invent
  new contours or imply a continuous seabed model. Keep soundings and depth
  areas separately addressable.
- Safety-contour selection is independent of visual contour thinning. The
  slider must not suppress a required safety contour; normative behavior needs
  the matching portrayal/navigation specifications.

### Consoles and activities (future CMS scope)

Retain these requirements for later. The initial core application must not
depend on CmsCore, roles, distributed locks, or a coordination service.

- Consoles have assigned roles with default activity sets.
- Activities can be released and claimed from any console.
- Navigation defaults to the Bridge role and permits concurrent claims.
- Other activities are exclusive unless explicitly declared shareable.
- Role defaults are assignment defaults, not hard-coded capability visibility
  restrictions. Permission rules beyond the established ownership model need
  a separate product decision.

### Tests

- Keep one independent test project per module.
- Keep civilian and military aggregate entry points separate.
- Run the actual applicable module suites through each aggregate entry point,
  not only shared smoke functions.

## 4. Architecture and ownership

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
       -> AIS / Radar / Sonar: input adapters and source observations
       -> Tracks: track types, authoritative track state and change feed

Future CMS modules: outside the initial core-application milestone

Reference/Data tooling -> chart public reader APIs, never the reverse
UI -> module services; chart decoding/domain models -> no Avalonia dependency
```

| Existing project (short names; see section 16 for directories, assemblies and module IDs) | Intended responsibility |
|---|---|
| Core | Main executable, application lifetime, runtime module loading/hot reload, feature registry, composition, diagnostic events |
| CoreUI | UI module: windows, shell/workspaces, panels, commands and diagnostics presentation; hosts Display's viewport |
| Display | Drawing module: chart viewport, projection/view transforms, feature layers, track symbols/overlays and hit testing |
| Charts | Data module: exchange discovery, version dispatch, S-101 reading, update lineage, immutable chart data and queries; no drawing |
| Navigation | Own-ship/navigation state, routes, CPA/TCPA and other navigation calculations; no drawing |
| AIS | AIS ingestion, decoding, source health, observations |
| Tracks | Track type definitions, creation/update/lifecycle, identity, source provenance, freshness, snapshots and change feed |
| Radar, Sonar | Receive/normalize source data and submit observations to Tracks; raw video/acoustic processing requires separate scope |
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
| AIS, Radar, Sonar | Host contracts and Tracks ingestion provider | Display is not required |

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
AIS / Radar / Sonar -> observations -> Tracks -> track snapshots/change feed
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

### Sensor-to-track contract proposal

- Each adapter submits a normalized `TrackObservation` to an ingestion contract
  owned by Tracks and receives an explicit accepted/rejected result and managed
  track ID when association succeeds. A source track ID is not the global ID.
- Observations carry source/module ID, source-local target ID when available,
  observation ID/sequence, measured UTC time, receipt time, coordinate frame,
  position and/or source measurement, velocity when known, quality/uncertainty,
  and source health. Use explicit units and reference frames.
- Tracks alone creates managed track IDs, associates observations, applies
  updates and publishes immutable track snapshots. Sensors do not construct
  authoritative tracks or call Display directly.
- Initially associate repeated observations by source plus source-local ID.
  Do not merge AIS/radar/sonar reports solely because positions are nearby.
  Cross-source correlation/fusion is separate policy requiring evidence/tests.
- Preserve bearing-only/range-only detections as measurements when applicable.
  Do not invent a geographic position without sensor pose, reference frame and
  sufficient data; such measurements need a supported estimator before they
  become chart-positioned tracks.
- Define duplicate/out-of-order handling, source-ID reuse, stale/lost/removed
  lifecycle, bounded queues and overload diagnostics. Distinguish source
  disconnect from confirmed target disappearance.
- Expose a revisioned snapshot plus ordered changes (upsert/remove/source state).
  Display and Navigation subscribe independently; a late/reloaded consumer
  resynchronizes from a snapshot and then applies changes without gaps.
- Feed cancellation/unsubscription and a bounded resync path are mandatory for
  hot reload. Keep authoritative track state in a versioned checkpoint during
  Tracks replacement; sensor reconnection must not create duplicate tracks.
- Navigation calculates CPA/TCPA from own-ship and target kinematics at a common
  time/reference frame. Return unavailable/qualified results for missing or
  stale motion, handle near-zero relative velocity, and test units, negative
  TCPA and motion assumptions. Constant-velocity CPA is an explicit model,
  not a guarantee of future separation.

### Deployment and dependency boundary

The proposed directory layout is anchored to the executable location, not the
process working directory:

```text
<application-directory>/
  <Core executable and host dependencies>
  modules/
    <feature-module binaries and their required deployment resources>
```

Proposed packaging: one package directory per module, containing its entry
assembly, a build-generated manifest, private dependencies, and resources.
Dropping a module means deploying the complete package, not assuming every
module is a self-contained single DLL. The manifest declares identity, entry
assembly, dependencies, contract compatibility, exports, and override targets;
it is package metadata, not runtime enable/disable configuration.

Publish new package contents to a staging location and make a completed package
visible using an atomic rename or explicit completion marker. Do not activate
intermediate compiler output or partially copied packages.

- Core must run without any optional module present, retaining host diagnostics
  and recovery rather than failing because CoreUI, Charts or CmsCore is absent.
  The normal shell is supplied by CoreUI when available.
- Core must not reference concrete feature-module projects or hard-code their
  implementations. Resolve capabilities through shared contracts registered by
  discovered modules.
- Modules contribute functionality to Core; they are libraries, not separately
  launched applications. Modules do not depend on the Core executable's
  concrete window or startup implementation.
- Shared contracts are host-supplied infrastructure, not independently
  discoverable feature modules. A small contracts library remains the proposed
  way to provide stable type identity without plugin-to-executable coupling.
- Build/publish packaging must place selected module artifacts and their
  dependencies into `modules` reproducibly. Do not require manual copying from
  arbitrary `bin` directories for every development run.
- Deploying a module makes its capabilities available subject to ownership
  rules; there is no separate module enable/disable configuration.

### Runtime discovery and lifecycle

The planned loader in Core will:

1. Resolve and inspect the `modules` directory at application startup. A missing
   directory represents no installed modules, not a requirement to download them.
2. Discover implementations of the shared module contract while distinguishing
   entry assemblies from supporting dependencies.
3. Validate module identity, compatible contract versions, and declared
   dependencies; detect duplicates, missing dependencies, and dependency cycles
   before activating affected modules.
4. Register and initialize modules in dependency order, then expose only
   successfully initialized services, capabilities, and UI contributions.
5. Report load/initialization failures with module and file context. Failed
   modules and their blocked dependents must not appear as available.
6. Stop and dispose initialized modules in reverse dependency order on shutdown.

Module binaries are executable code and must come from a trusted deployment
source; loading contexts are not security sandboxes.

### Required hot-reload behavior

Proposed implementation:

- Watch `modules` for changes, debounce notifications, and reconcile against
  complete package inventories. Watcher events are hints, not an ordered change
  log; recover from overflow/missed events with a rescan. Provide explicit
  rescan/reload actions in diagnostics for troubleshooting.
- Copy each accepted package generation into a private shadow directory before
  loading. This avoids locking deployed DLLs on Windows and gives Windows and
  Linux the same stable-generation behavior. Do not shadow-copy mutable user
  data or share the runtime cache between application instances.
- Load each module generation in a collectible `AssemblyLoadContext`, resolving
  private dependencies from its package. Shared contracts must use host-owned
  type identity. Keep Avalonia/runtime assemblies shared rather than loading
  conflicting framework copies into each module.
- Stage and validate the candidate registration graph without changing active
  registrations. Identify affected dependents and override providers; quiesce
  their work, cancel/drain in-flight operations, and detach UI contributions on
  the UI thread before committing the replacement.
- Route cross-module calls through host-owned, generation-aware contracts or
  service handles, not indefinitely retained concrete plugin instances. Do not
  allow an operation to mix old and new providers halfway through execution.
- State transfer uses versioned, host-owned data contracts or durable storage,
  never plugin-specific objects retained across unload. The module declares
  whether state can be migrated; incompatible state blocks replacement with an
  explicit diagnostic rather than silently resetting user work.
- Commit the validated provider graph as one generation, then initialize/resume
  affected work according to its lifecycle contract. Keep a last-known-good
  package and state checkpoint; if activation fails, restore that generation
  where possible. A failed rollback leaves affected features explicitly
  unavailable, never reporting successful recovery.
- On removal, stop affected consumers and recompute providers. Restore an
  available base provider when a patch disappears; otherwise show the feature
  as unavailable and explain blocked dependencies.
- Unsubscribe events, stop tasks/timers, release device/file handles, remove
  views and service references, and dispose the old generation. Collectible
  contexts unload cooperatively; verify eventual collection and report retained
  generations rather than accumulating them silently.
- If quiescing times out, reject/defer the reload and preserve the current
  generation where safe. Do not forcibly unload executing code or promise
  seamless replacement of arbitrary third-party/native libraries.

Modules must satisfy this lifecycle to be accepted as hot-reloadable packages.
Core, the shared contract ABI, and the desktop runtime are infrastructure:
updating them may require an application restart. Native components that cannot
be reliably unloaded need an isolated restartable worker or an explicit
unsupported-package diagnostic; they must not silently weaken the module
hot-reload requirement.

Activity ownership remains authoritative across reload. Suspend affected
actions while their provider changes; a reload must not release/reassign locks
or enable commands on a console that does not own the activity.

### Feature override contract

- Export features under stable feature IDs and versioned contracts. Each module
  has a unique ID even when replacing a feature from another module.
- Declare overrides explicitly: target feature, compatible target
  module/feature versions, replacement export, and ordering relationships.
  Do not use filesystem enumeration, timestamps, or "last DLL loaded wins."
- A replacement implements the same public contract. Consumers resolve the
  effective provider through Core's feature registry; they do not instantiate
  the original implementation directly.
- Resolve ordering as a directed graph. Explicit ordering can establish a
  patch chain; incompatible contracts, cycles, and multiple unordered competing
  replacements reject the candidate change with actionable diagnostics. Leave
  the previous valid graph active instead of choosing an arbitrary winner.
- Replacement is the initial override mechanism. Decoration/chaining requires
  a separate explicit contract; never infer "call previous implementation."
- Preserve the original provider for rollback, but only one effective provider
  serves a replacement-style feature in a committed generation.
- Diagnostics show installed providers, override relationships, effective
  provider/version, generation, and why a candidate was rejected. Removing a
  patch recomputes the graph without requiring enable/disable switches.
- Features exposed by Core may be overridable only through explicitly declared
  extension points. Module loading, contract validation, ownership enforcement,
  and trust boundaries are host invariants, not replaceable by ordinary patches.
  Arbitrary binary detours or patching private host methods are not the design.

### Windows and Linux support

- Build, publish, and run the same managed application/module contracts on both
  platforms. Use Avalonia and portable .NET APIs; isolate unavoidable OS/device
  integration behind adapters with explicit platform capability diagnostics.
- Use executable-relative module discovery, portable path APIs, consistent
  filename casing, and package-relative resource resolution. Test case-sensitive
  Linux filesystems as well as Windows file-locking and rename behavior.
- Package native resources by supported runtime identifier and resolve the
  correct variant. Native library availability is not implied by a managed
  module loading successfully.
- Verify filesystem watching, shadow copies, file permissions, writable cache/
  user-data locations, cleanup, and UI lifecycle behavior on both systems.
- Run build/tests and publish smoke tests on Windows and Linux; include actual
  GUI smoke coverage on each. Select supported Linux distributions, windowing
  environments, CPU architectures, and hardware during baseline work.
- No macOS packages, integration work, or compatibility guarantees are planned.

### Module contract

Start with stable module ID, module version, compatible contract version,
declared dependencies, capabilities, exported features, and override
declarations. Define staging, activation, quiesce, state transfer, rollback,
UI contribution removal, and disposal contracts before implementing the first
reloadable feature. Capability descriptors refer to activity policies rather
than implementing locks themselves.

Preserve existing IDs unless a migration is intentional. Package private module
dependencies coherently; do not load a separate copy of the shared contract
assembly per plugin. Test dependency resolution and duplicate detection.

## 5. Chart ingestion and storage contracts

The ingestion and data-storage pipeline in this section belongs to Charts. Core
provides generic hosting and UI contribution points, not catalogue parsers,
ISO 8211 decoding, or S-101 feature logic. Charts registers data/import services;
CoreUI binds their commands and Display draws their data. Without Charts, those
features are unavailable rather than supplied by a built-in fallback reader.

Implement the boundaries proposed in the S-101 mapping as typed C# APIs:

1. Exchange discovery reads catalogue namespaces, product versions, dataset and
   support references, and trust/protection metadata.
2. A bounded ISO 8211 parser decodes records using the applicable encoding
   specification, preserving field/record location for diagnostics.
3. S-101 decoding resolves feature/information types, nested and repeated
   attributes, topology, geometry, coordinate scaling, CRS, and vertical datum.
4. A lineage service validates editions and update sequence, applies operations
   transactionally, and publishes a new immutable dataset snapshot only after
   successful validation.
5. A chart store exposes spatial/scale queries and durable import provenance.
   Begin with the simplest store supporting measured fixture sizes; choose a
   persistent spatial database only when measurements justify it.
6. Display, not Charts, builds scenes from chart queries and view state, not raw
   parser records. Full S-101 portrayal requires the matching portrayal resources and
   rules; a basic feature viewer must be labelled as such.

### Cross-cutting invariants

- Persist source file identity/hash, dataset ID, edition/update, product,
  encoding/catalogue versions, import time, and diagnostic provenance.
- Distinguish rejected, unsupported, cancelled, and successfully loaded data.
  Unknown or unsupported versions are never treated as an empty successful cell.
- Preserve unknown coded attributes with diagnostics; reject unsafe-to-interpret
  geometry or unsupported encodings explicitly.
- Resolve catalogue paths inside the selected exchange root. Account for the
  observed `file:/S100_ROOT/...` and `file:/S-101/...` reference conventions
  without treating them as arbitrary host filesystem paths.
- Disable external XML entity resolution and implicit schema/network downloads.
  Bound record sizes, resource counts, nesting, and memory consumption.
- Do not trust a file because it has a signature. Separate signature parsing,
  cryptographic verification, certificate trust, expiry, and navigation policy.
  Test-only acceptance of self-signed artifacts must be explicit and visibly
  non-operational.
- Imports are cancellable and asynchronous; long parsing work never blocks the
  Avalonia UI thread. Failed imports leave previously installed charts intact.
- Chart caches are derived data, invalidated by source/version changes.
- Keep offline operation possible; remote schema/catalogue retrieval is not an
  implicit requirement for loading a supported package.

### Specification gate

Before low-level parser implementation, obtain and pin the exact S-100/S-101
specifications, feature catalogues, encoding rules, and required portrayal
resources for each supported edition. Distinguish a verified supported version
from a candidate fixture version. Audit the existing mappings against binary
headers and resource metadata; folder names and autogenerated YAML do not prove
wire-format conformance. Select an initial fixture only after that audit.

## 6. UI and console interaction

CoreUI supplies the shell and panels; Display supplies the chart viewport and
drawing. Charts supplies data; Navigation supplies calculation results; Tracks
supplies managed track state. UI controls call module contracts rather than
implementing parsing, track mutation or CPA in view models.

The first integrated shell should provide:

- chart viewport with pan/zoom, scale indication, cursor position, and visible
  loading/error state;
- chart manager for import, installed editions/updates, lineage and diagnostics;
- layer panel with locked land/coastline base, overlay controls, and contour
  interval slider;
- feature inspection with decoded values, source identity, and geometry;
- diagnostics/module-status panel retaining current loader visibility.

Later add own-ship/track overlays, route tools, activity status, and console
claim/release controls. View state belongs to a console; installed datasets and
authoritative activity ownership have explicit shared-state boundaries.

### Multi-console coordination proposal (long-term CMS backlog)

This section preserves future design context only. It is not initial-core work.

Start with a single-process coordinator behind an interface. For multi-console
deployment, use one authoritative coordination service with atomic claim/release
operations and monotonically versioned state; do not implement locks with local
UI flags or peer broadcasts.

Proposed activity model: activity ID, concurrency policy, default roles,
owners, claim revision, and availability derived from deployed capabilities.
An exclusive claim rejects competing owners; concurrent Navigation claims
retain all owners. Releases verify the current owner and claim revision.

Network topology, authentication, persistence, lease duration, offline behavior,
and failover require confirmation before distributed implementation. Never
silently reassign an exclusive activity on disconnect; show unknown/unavailable
ownership until the agreed lease/recovery protocol resolves it.

## 7. Milestones and acceptance gates

All milestones are pending. **M1 is the initial core application**, delivered
through the work packages below. These supersede the earlier M0-M8 numbering.
Do not implement CMS while working on M1.

| Work package | Prerequisite | Deliverables and exit evidence |
|---|---|---|
| M1-A: Host and contracts | None | Restore shared types; keep Core as executable and modules as libraries; hot reload/overrides and repeatable packaging; no-module startup; replacement/removal/rollback tests on Windows and Linux; independent core-module tests |
| M1-B: UI and viewport | M1-A | CoreUI owns normal UI; Display contributes empty viewport and layer API; UI/Display reload without restarting Core; diagnostics/recovery remain available |
| M1-C: Chart-data pipeline | M1-A | Audit specifications/resources, pin fixture versions; Charts decodes a real base cell; verify metadata, attributes, topology and malformed-input diagnostics |
| M1-D: Integrated chart view | M1-B, M1-C | Display draws Charts data; pan/zoom, locked base, overlays, contour slider and feature inspection; demonstrate Charts/Display provider replacement |
| M1-E: Track pipeline | M1-A; UI integration needs M1-B | Tracks contracts/manager plus AIS/Radar/Sonar adapter boundaries; recorded/simulated observations produce managed tracks and Display updates; lifecycle, resync and reload tests |
| M1-F: Navigation functions | M1-E | Own-ship state and tested CPA/TCPA; Display annotations; unavailable/stale/degenerate-motion behavior is explicit |
| M1-G: Integration gate | M1-D, M1-E, M1-F | Windows/Linux publish and smoke tests; actual civilian module suites; dependency, override and hot-reload failure tests across the integrated application |

M1-C and M1-E can proceed independently after M1-A. The M1-G gate requires no
CMS binaries. Device-specific protocols and hardware integrations are selected
separately; initial simulated adapters do not imply production device support.
M1 is a development core application, not certified ECDIS.

Follow-on core work includes full chart installation/update management,
expanded portrayal and real sensor protocols. Preserve the update-validation
requirements in this plan; agree their release scope after the first integrated
slice. CMS roles, multi-console coordination and specialized modules remain
long-term backlog and do not block core development.

## 8. Verification strategy

- Module tests: pure domain/parser tests plus focused integration tests. Add UI
  interaction tests only for behaviors requiring the actual view layer.
- Loader/deployment tests: start Core with no modules, then deploy a test module
  into `modules` and verify discovery, initialization and contribution without
  recompiling Core. Cover incompatible contracts, missing/private dependencies,
  duplicate IDs, dependency cycles, initialization failures and orderly disposal.
  Verify the same path resolution when launched from a different working
  directory. Verify chart functionality appears only when Charts loads.
- Hot-reload tests: add/replace/remove modules during a running session without
  restarting Core; assert the effective provider and behavior actually change.
  Cover partial copies, burst/missed watcher events, state migration, in-flight
  work, UI detachment, dependent reloads, rollback failure and unload timeouts.
  Repeat reload cycles and verify old contexts/resources are released.
- Override tests: base provider, compatible patch, explicitly ordered patches,
  ambiguous replacements, cycles, incompatible versions, patch removal and
  fallback. Verify ownership checks still apply after replacement.
- Run the loader/reload/override scenarios on Windows and Linux, including
  Windows replacement locks and Linux path casing/permissions. Verify a failed
  candidate does not partially publish registrations or lose user state.
- Chart tests: positive fixture assertions for exact IDs, metadata, geometry,
  nested values and reference resolution; negative tests for malformed records,
  missing resources, unsupported versions and interrupted imports.
- Updates: verify installed snapshot identity and feature changes, not only
  whether a method returned success. Exercise S-164 sequence/reissue/cancellation
  scenarios against approved expected results.
- S-158: map validation cases and report expectations individually; filenames
  containing "errors" are not sufficient pass/fail definitions.
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
- Separate tiny deterministic fixtures from optional full-corpus runs. Missing
  submodule data must be an explicit setup error in data-integration runs, not
  a passing test with zero assertions.
- Record fixture revision and selected specification versions with results.
- Measure load time, memory, query latency and pan/zoom frame time on named
  fixture/hardware combinations; establish numeric budgets from the first
  working slice instead of inventing unsupported performance claims.

## 9. Decisions still requiring confirmation

These do not block saving the plan or M1-A, but must be resolved before their
dependent milestones:

| Decision | Provisional position | Resolve before |
|---|---|---|
| Release scope | Initial core application confirmed; agree depth of device integration and follow-on chart update support | M1-G scope review |
| Platform details | Windows and Linux confirmed; macOS excluded. Select Linux distributions/windowing environments, architectures and hardware | M1-A test/publish matrix |
| Product/spec versions | Select from audited available fixtures; no blanket S-100 support | M1-C |
| Portrayal/certification | Development viewer, not certified ECDIS or operational navigation | Any operational claim |
| Inputs/protocols | Recorded and simulated observations before live devices; select AIS/Radar/Sonar device protocols separately | M1-E and later device integration |
| Route/navigation behavior | Navigation owns route state/calculations; Display owns map interaction; Charts supplies chart data | M1-F and later route tools |
| Console topology | Deferred with CMS; preserve host extension points without implementing coordination now | Future CMS milestone |
| Trust/licensing | Verify dataset/resource usage rights and trust requirements | Redistribution or operational use |
| CMS scope | Preserve extension boundaries; no operational weapon functions in initial scope | Any CMS feature expansion |

## 10. Next implementation session

1. Re-read this memory and inspect the worktree without overwriting existing work.
2. Execute M1-A: resolve missing shared types and establish a core-only build/test
   baseline. Convert CoreUI, Display and Core Sonar scaffolds to module libraries.
3. Decide the minimal shared-contract boundary using existing loader behavior
   and tests; preserve file-drop deployment and implement lifecycle/override
   contracts early enough to prove hot reload on Windows and Linux in M1-A.
4. Establish CoreUI/Display boundaries (M1-B), then audit chart fixtures (M1-C)
   and define Tracks-owned ingestion/feed contracts (M1-E). Do not start CMS.
5. Update this file with verified milestone results, decisions and next steps.

The sections above originated as planning-only documentation. Implementation
has now started as recorded below.

## 11. Implementation progress: reloadable host foundation

### Implemented

- Added a UI-independent `Poseidon.Contracts` assembly for module identity,
  manifests, lifecycle, versioned state, runtime status and feature invocation.
- Core modules no longer reference the application executable. CoreUI, Display
  and Core Sonar now build as libraries; Core remains the sole executable.
- Added Core's module runtime: completed-package discovery, SHA-256 package
  fingerprints, per-generation shadow copies, collectible loading contexts,
  dependency ordering and exact-version checks.
- Added deterministic feature replacement with explicit target/version,
  ambiguity/cycle rejection, and effective-provider diagnostics.
- Added automatic watcher/debounced rescan plus periodic reconciliation and a
  UI rescan action. Candidate graphs are validated before stopping active code.
- Added state capture, bounded lifecycle operations, rollback to prior instances,
  and explicit fault/quarantine behavior when safe recovery is impossible.
- Moved the normal diagnostics window into CoreUI. Core retains a minimal
  recovery window and application bootstrap; CoreUI replacement recreates the
  window in the same application process.
- Added `Core.Application.proj` to build/package the host and eight core modules.
  Module manifests and completion markers are emitted without module toggles.
- Added independent CoreUI, Display and Core Sonar test projects. The civilian
  aggregate now executes every core module test suite, not just smoke helpers.
- Preserved existing legacy IDs (`CMS.AIS`, `CMS.Tracks`, etc.) for compatibility;
  their names do not introduce dependencies on future CMS functionality.
- Added Windows/Ubuntu CI build/test jobs and root build/deployment documentation.

### Verified evidence

- Windows: core application build succeeds with zero warnings/errors.
- Windows: civilian aggregate executes all nine core-module suites successfully.
  Latest run: 31 passing tests in total (22 Core tests, eight other module
  checks and one aggregate smoke check); no failures or skips.
  Runtime tests exercise add/replace/remove, state preservation, explicit patch
  chains and fallback, failed activation rollback, invalid graphs, incomplete
  packages, uncooperative lifecycle deadlines, in-flight operations, automatic
  watching and repeated context collection.
- Desktop smoke: launched the actual application from a different working
  directory, observed CoreUI generation 1, triggered package reconciliation,
  observed generation 2 in the same process, and closed with exit code 0.
- Host publish succeeds. Packaged module deployment is provided separately by
  the build orchestration as documented in the root README.
- The collection test exposed a plugin-type serialization cache retaining
  collectible assemblies; the fixture now parses configuration without placing
  its private types into the global JSON metadata cache.
- Linux execution is not locally verified: WSL/Linux is unavailable on this
  machine. The CI matrix is configured but has not been run in this session.

### Scope and remaining gates

M1-A is **in progress**, not complete on both platforms. The initial design
deliberately reloads the entire module generation behind a serialized feature
barrier. Selective subgraph reload and asynchronous/streaming feature leases are
not implemented. Removing a required provider while leaving dependents installed
rejects the candidate graph rather than automatically blocking only those
dependents. Version constraints currently match exact versions.

CoreUI supplies a diagnostics shell only. Display/Charts/Navigation/Tracks and
sensor projects remain functional scaffolds; no chart reader, viewport renderer,
track manager, CPA/TCPA service or live device interface is claimed.
Military aggregation/CMS work remains deferred.

Next: verify the Windows/Linux CI results, extend the contract tests and package/
lifecycle failure coverage as needed, then implement the Display viewport
contribution boundary (M1-B), chart specification audit (M1-C), and Tracks
ingestion/change-feed contracts (M1-E).

## 12. Foundation test expansion

Added systematic coverage for implemented behavior, not placeholder chart,
track, sensor or navigation functionality:

- Dedicated shared-contract tests for default lifecycle/state rejection,
  manifest/status serialization and stable module identities.
- Package-validation unit tests for malformed/invalid metadata, dependencies,
  exports, deterministic content fingerprints and staging consistency.
- Runtime regression tests for unchanged/incomplete packages, invalid assemblies
  and exports, cancellation, recursive invocation, dependency removal, lifecycle
  ordering, state-capture failure, rollback/cleanup failure, native dependencies,
  shutdown and disposal.
- Each core module independently tests its entry point against its deployment
  manifest, library output, declared dependencies, capabilities, exports and
  absence of concrete implementation references.
- Headless Avalonia tests exercise CoreUI commands, displayed diagnostics, user
  close versus module stop, generation refresh/restart, unsupported state, and
  host recovery-window visibility/failure reporting.
- Coverage configuration includes production assemblies and excludes test/
  fixture assemblies. All module projects have the collector needed to run it;
  Windows/Linux CI now requests coverage.

Tests reproduced and fixed two shutdown defects: stale public module/feature
status after disposal and inability to retry cleanup after a drain timeout.
Entry-assembly path validation now rejects both slash conventions consistently
on Windows and Linux. The recovery window consumes the existing host interface,
rather than depending on the concrete runtime, to make its behavior unit-testable.

Latest Windows aggregate: **142 passing tests**, zero failures/skips, across
the nine core module projects, shared contracts and aggregate smoke check.
Measured merged line coverage: shared contracts and CoreUI 100%, module graph
100%, package/loading code 99%, module runtime 96.1%, recovery window 100%.
Overall Core assembly coverage is 88.6%; application bootstrap and some
OS-error/timeout cleanup paths are not covered. Scaffold modules' declaration/
lifecycle coverage is 100%, which does not imply their future domain behavior
is implemented. No blanket 100% coverage or Linux execution claim is made.

CMS remains deferred. These changes do not repair or validate its old test
project paths or add operational CMS behavior. Real desktop smoke tests remain
necessary in addition to headless tests.

## 13. Startup splash

Core now owns a borderless, centered black splash window with bold white
"Poseidon" text. It is shown before background module discovery/loading and
closed when initial loading completes, including failures. Recovery UI is not
created until that initial attempt finishes, preventing it from appearing over
the splash. CoreUI continues to own the normal application window.

Headless tests verify appearance, visibility throughout pending loading,
closure on completion and error propagation/closure on failure.
The focused UI suite passes all 15 tests; application/module build passes with
zero warnings/errors. No artificial minimum splash duration is imposed.

## 14. Manifest-driven, fault-isolated module discovery

Problem: no module loaded in practice. Entry points hard-coded legacy IDs and
`1.0.0`, the manifests had moved to `Core.*` / `0.0.1`, and a single mismatch
rejected the whole generation.

Decisions (user-confirmed):
- `module.json` is the only source of identity. `IModule` no longer has
  `ModuleId`/`ModuleVersion`, so modules are discovered dynamically with no host
  or code changes when an ID or version changes.
- Well-known IDs: `Core.Display`, `Core.Charts`, `Core.Navigation`, `Core.Tracks`,
  `Core.AIS`, `Core.Radar`, `Core.UI`, and `Core.Sonar` (civilian sonar
  input: echo sounder, NDS). `CMS.Sonar` remains the separate military module.
- Dependency versions are a minimum within the same major version
  (`ModuleDependency.IsSatisfiedBy`). Feature export versions still match exactly.

Runtime behavior (`ModuleRuntime`, `ModuleGraph`):
- Each package is loaded and validated individually.
- `RuntimeStatus.Packages` reports each package as Active, Retained, Pending,
  Failed or Blocked, with a reason.
- Failed: duplicate IDs, invalid packages, declaration mismatches, ambiguous
  overrides, start failures.
- Blocked: transitive dependents of anything unavailable, and cycles.
- An invalid or incomplete update keeps the last-known-good shadow copy running
  (Retained).
- A start failure excludes that package and starts the rest.
- Timer reconciliation does not retry unchanged rejected packages. An explicit
  rescan or `ReloadAsync` does.
- CoreUI shows the package list, and the recovery window lists non-active
  packages.

Verified 2026-10-24:
- Build: zero warnings.
- Civilian aggregate: all suites pass, including Core.Tests (88) and
  CoreUI.Tests (19).
- Military aggregate: passes, after fixing stale CMS test project references
  `System\CMS\<Name>` -> `System\CMS\Cms<Name>`.
- Desktop smoke test on Windows: all 8 deployed Core packages are Active at
  0.0.1 in the CoreUI window, generation 1, with no diagnostics.
- New tests cover: a broken package next to healthy ones; Blocked dependents; a
  start failure on initial load; Retained last-known-good; any manifest
  ID/version being authoritative; minimum-version dependencies; a missing
  modules directory; and explicit reload retry.
- Linux is not yet verified.

## 15. Extension points and contributions (supersedes ModuleIds and `exports`)

Decisions (user-confirmed):
- `ModuleIds` is removed. The host makes no assumption about which modules are
  loaded; module IDs are plain strings owned by manifests.
- Manifests declare what a module changes: `provides` (extension points it
  opens, with version, `single`/`multiple` cardinality and an optional shared
  contract interface) and `contributions` (point, feature ID, targeted point
  version, optional `replaces` override with `priority`). UI is just a
  contribution to a UI point. `exports` is gone and unknown fields are rejected.
- The host owns one point, `shell.host` (`IShell`). The recovery window is
  shown when no module contributes to it, instead of checking for `Core.UI`.
- Overrides target a feature ID, not a module. Highest priority wins; equal top
  priorities are ambiguous and excluded. A replacement needs an original with the
  same version and a compatible contract, else it is Blocked.
- A contribution to a point nobody provides, or targeting an incompatible point
  version, is Blocked; a missing contract implementation, duplicate point
  declaration or `single` point conflict is Failed. Start order follows
  dependencies, point providers and replaced originals.
- `RuntimeStatus.Features[].PointId` reports the target point;
  `RuntimeStatus.HasContribution(point)` is the host query.
- Deferred: `IModule.Capabilities` still exists. It should be folded into
  contributions once the owning modules (Charts, Display, CmsCore) define their
  point contracts, since contributions need a typed interface to execute.

## 16. Module naming and directory layout

Decisions (user-confirmed): a module's directory, project file, assembly,
deployed package folder and manifest `entryAssembly` all share one name, and the
module ID is that name with a dot after the group prefix.

| Directory | Project / assembly / package folder | Module ID |
|---|---|---|
| `System/Core/CoreUI` | `CoreUI` | `Core.UI` |
| `System/Core/CoreDisplay` | `CoreDisplay` | `Core.Display` |
| `System/Core/CoreCharts` | `CoreCharts` | `Core.Charts` |
| `System/Core/CoreNavigation` | `CoreNavigation` | `Core.Navigation` |
| `System/Core/CoreTracks` | `CoreTracks` | `Core.Tracks` |
| `System/Core/CoreAIS` | `CoreAIS` | `Core.AIS` |
| `System/Core/CoreRadar` | `CoreRadar` | `Core.Radar` |
| `System/Core/CoreSonar` | `CoreSonar` | `Core.Sonar` (civilian sonar input) |

- `System/Core/Core` is the host executable, not a module. It stays `Core`.
- CMS modules already follow the pattern with `Cms` casing (`System/CMS/CmsCore`,
  `CmsComms`, `CmsEW`, `CmsGun`, `CmsMissile`, `CmsRadar`, `CmsSonar`,
  `CmsTracks`, `CmsUI`). They have no manifests yet; CMS remains deferred.
- The `module.json` next to each project holds the ID, version, `entryAssembly`
  (`<Directory>.dll`), dependencies, and any `provides`/`contributions`
  (section 15). Only `CoreUI` contributes so far (`shell.host`); the other core
  modules declare no points yet because their contracts are not defined.
- `Core.Application.proj` builds the host and deploys each module to
  `modules/<Directory>`. Test projects keep short names (`AIS.Tests`); the shared
  test targets map them to `System/Core/Core<Name>/module.json` (`CoreUI.Tests`
  maps to `CoreUI`). C# namespaces are unchanged (`Poseidon.Modules.<Name>`).
- After renaming a module, delete stale `modules/<OldName>` folders in build
  output: an old and a new package with the same module ID are both rejected as
  duplicates.

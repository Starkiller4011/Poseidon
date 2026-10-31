# M1-A: Host and module loading

Milestone: M1-A (prerequisite: none)
Status: Done for its automated gate (2026-10-26). Windows, Arch Linux and
RHEL-compatible CI passed (run 40315088820). Native Linux desktop smoke testing
is deferred to M1-G by decision. Not a certification claim.

This file holds the host, contract, runtime, packaging and hot-reload material
moved verbatim from the former application plan: the pre-implementation
baseline (section 2), the host design (parts of section 4), the implementation
progress and decisions (sections 11 to 16) and the M1-A ledger rows (section 17).
Former section numbers are kept in headings so older references remain
traceable (see the section map in the master plan). References to sections 18
and 19 now point to [m1-b-ui-and-viewport.md](m1-b-ui-and-viewport.md) and
[m1-e-track-pipeline.md](m1-e-track-pipeline.md).

## 2. Pre-implementation baseline

This section records the starting state, not the current implementation status.
See section 11 for changes made during implementation.

The [solution](../../../../Poseidon.slnx), re-inspected after restructuring, contains
33 projects: 18 application/module projects, 12 existing module test projects,
two aggregate test projects, and one reference-data console project.
CoreUI, Display, and Core Sonar now exist; corresponding independent test
projects are still needed. CMS projects have been renamed/expanded; their
test-project alignment can be addressed when CMS work resumes.

| Area | Observed state | Planning consequence |
|---|---|---|
| Runtime | Projects target .NET 10; desktop host uses Avalonia 11.3.7 | Keep this stack initially; establish a reproducible SDK/build baseline |
| Desktop host | [Core](../../../../System/Core/Core/Core.csproj) is a WinExe with a module-loader status window | Retain application bootstrap in Core; move normal UI to CoreUI |
| Startup | [App](../../../../System/Core/Core/App.axaml.cs) calls `FileDropModuleLoader` | Preserve file-drop discovery as the intended deployment model |
| Modules | Module classes declare IDs, versions, and capabilities | Functional services, UI contributions, and lifecycle remain to be designed |
| Shared types | Source references `Poseidon.Core.Modules` and `Poseidon.Domain.Modules`, but their defining source files were not present in the inspected tree | Audit and restore/implement contracts before assuming the solution builds |
| Dependency direction | Older module projects reference the Core executable; newer scaffolds have no such reference | Establish shared contracts instead of implementation/host references |
| New module output | CoreUI, Display, and Core Sonar currently declare executable output | Convert these to module libraries during foundation implementation |
| Chart data | Submodule and preliminary mapping documents exist | Build an actual ISO 8211/S-101 pipeline; do not substitute YAML fixtures |
| Reference tool | [Data](../../Data.csproj) is a console scaffold with a Hello World entry point | Reserve for inventory, inspection, and fixture tooling, not a production dependency |
| Tests | xUnit projects contain module-ID smoke checks | Add behavioral tests alongside existing checks |
| Aggregates | Aggregate facts explicitly call static smoke helpers | Referencing test assemblies does not execute their complete suites |
| Civilian suite | Currently references Core, Charts, AIS, and Tracks tests, but not Navigation or Radar | Reconcile civilian suite membership with intended civilian functionality |
| Dataset project item | Core includes a relative dataset glob pointing outside the current reference-data location | Correct/remove it during implementation; do not bundle the entire corpus into runtime output |

No build or test run was performed for this documentation task. The missing
source and project-wiring observations are prerequisites to investigate, not
newly verified compiler failures.

## Module and deployment requirements (from former section 3)

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

## 4. Host design (subsections of the former architecture section)

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

### Windows, Arch Linux, and RHEL support

- Build, publish, and run the same managed application/module contracts on
  Windows and the supported Linux distributions. Use Avalonia and portable
  .NET APIs; isolate unavoidable OS/device
  integration behind adapters with explicit platform capability diagnostics.
- Use executable-relative module discovery, portable path APIs, consistent
  filename casing, and package-relative resource resolution. Test case-sensitive
  Arch Linux and RHEL filesystems as well as Windows file-locking and rename
  behavior.
- Package native resources by supported runtime identifier and resolve the
  correct variant. Native library availability is not implied by a managed
  module loading successfully.
- Verify filesystem watching, shadow copies, file permissions, writable cache/
  user-data locations, cleanup, and UI lifecycle behavior on all three targets.
- Run build/tests and publish smoke tests on Windows, Arch Linux, and RHEL;
  include actual GUI smoke coverage on each. Select supported Linux windowing
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

## Loader and hot-reload verification (from former section 8)

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
- Run the loader/reload/override scenarios on Windows, Arch Linux, and RHEL,
  including Windows replacement locks and Linux path casing/permissions. Verify
  a failed
  candidate does not partially publish registrations or lose user state.

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
- Added root build/deployment documentation and a GitHub Actions workflow for
  pull requests and manual dispatches (not on push). It runs a native Windows build,
  full solution tests, civilian coverage and packaging, plus Arch Linux and
  Red Hat UBI 9 container build/test/package jobs. UBI 9 is the initial
  RHEL-compatible gate, not a substitute for native RHEL desktop validation.

### Verified evidence

- Windows: core application build succeeds with zero warnings/errors (verified
  locally on 2026-10-26).
- Windows: the exact CI restore, Release build, full-solution test, civilian
  coverage, host publish and eight-module staging commands pass locally.
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
- Cross-platform CI: GitHub Actions run
  [40315088820](https://github.com/Starkiller4011/Poseidon/actions/runs/40315088820)
  (workflow run #2, commit `ba840d2`, 2026-10-26) passed all four jobs: Windows
  build/test/coverage, Arch Linux container, RHEL 9 compatible (UBI 9)
  container, and Windows package. Each platform passed the complete solution:
  213 distinct tests across 21 test projects (Contracts 14, Core runtime 114,
  CoreUI 20, other Core modules 54, CMS 9, aggregates 2) with no failures or
  skips, using .NET SDK 10.0.403 / runtime 10.0.14 on Linux. Each Linux job
  also published the host and staged all eight modules; package, TRX and
  coverage artifacts were uploaded.
- Earlier CI runs failed only in distribution bootstrap (no product failures):
  invalid package names (`libuuid` on Arch, `libunwind` on UBI 9), then a
  `curl` versus preinstalled `curl-minimal` conflict on UBI 9.
- Container jobs do not cover SELinux enforcement, a real display server or
  desktop environment. Native Arch Linux/RHEL desktop smoke tests are moved to
  the M1-G integration gate.

### Scope and remaining gates

M1-A is **complete** for its automated gate (2026-10-26): the host, contracts,
hot reload/overrides, packaging and replacement/removal/rollback tests pass on
Windows, Arch Linux and RHEL-compatible CI. Native Linux desktop smoke testing
is deferred to M1-G by decision, not omitted. The initial design
deliberately reloads the entire module generation behind a serialized feature
barrier. Selective subgraph reload and asynchronous/streaming feature leases are
not implemented. Removing a required provider while leaving dependents installed
rejects the candidate graph rather than automatically blocking only those
dependents. These are accepted, documented limitations rather than open M1-A
gates. Dependency versions use minimum-within-major matching (section 14).

CoreUI supplied a diagnostics shell only at this point (M1-B later added the
hosted viewport; see [m1-b-ui-and-viewport.md](m1-b-ui-and-viewport.md)).
Display/Charts/Navigation/Tracks and
sensor projects remain functional scaffolds; no chart reader, viewport renderer,
track manager, CPA/TCPA service or live device interface is claimed.
Military aggregation/CMS work remains deferred.

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
  Windows CI requests coverage. Arch Linux and RHEL-compatible jobs run the
  complete test solution and retain their TRX results.

Tests reproduced and fixed two shutdown defects: stale public module/feature
status after disposal and inability to retry cleanup after a drain timeout.
Entry-assembly path validation now rejects both slash conventions consistently
on Windows, Arch Linux, and RHEL. The recovery window consumes the existing host interface,
rather than depending on the concrete runtime, to make its behavior unit-testable.

Latest Windows aggregate: **142 passing tests**, zero failures/skips, across
the nine core module projects, shared contracts and aggregate smoke check.
Measured merged line coverage: shared contracts and CoreUI 100%, module graph
100%, package/loading code 99%, module runtime 96.1%, recovery window 100%.
Overall Core assembly coverage is 88.6%; application bootstrap and some
OS-error/timeout cleanup paths are not covered. Scaffold modules' declaration/
lifecycle coverage is 100%, which does not imply their future domain behavior
is implemented. No blanket 100% coverage or Arch Linux/RHEL execution claim is made.

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
- Additional operational requirement tests cover: restaging a retained module
  from its last-known-good shadow while another module changes; reaping retired
  shadow directories after collectible contexts are released; periodic
  reconciliation after watcher events are missed; suppressing automatic retries
  for unchanged rejected packages while preserving explicit retry; and
  restart-required package status after unsafe rollback failure. Package
  fingerprint tests also require rejection of file or directory symbolic links
  and reparse points without traversing content outside the package boundary.
- Arch Linux and RHEL are not yet verified.

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
  (section 15). `CoreUI` contributes `shell.host`, and `CoreDisplay` declares
  `display.viewport` and contributes its viewport to it (see
  [m1-b-ui-and-viewport.md](m1-b-ui-and-viewport.md));
  `CoreTracks` declares `tracks.ingestion` and `tracks.feed`, and `CoreAIS` and
  `CoreRadar` declare `ais.input` and `radar.input` (see
  [m1-e-track-pipeline.md](m1-e-track-pipeline.md)); the other
  core modules declare no points yet because their contracts are not defined.
  `CoreSonar` has no dependencies and is not a track source.
- `Core.Application.proj` builds the host and deploys each module to
  `modules/<Directory>`. Core test projects mirror the system names
  (`CoreAIS.Tests`, `CoreCharts.Tests`, and so on); the shared test targets map
  each project directly to `System/Core/<SystemName>/module.json`. C# module
  namespaces remain `Poseidon.Modules.<Name>`.
- After renaming a module, delete stale `modules/<OldName>` folders in build
  output: an old and a new package with the same module ID are both rejected as
  duplicates.

## 17. Requirement-to-test ledger (M1-A rows)

A requirement is ready for production work only after its acceptance test
exists and has been observed failing for the intended missing behavior.

| Requirement | Acceptance test or suite | Verification |
|---|---|---|
| A broken deployed package remains available from its last-known-good shadow while another module changes | `BrokenPackage_IsRestagedFromLastKnownGoodShadowDuringAnotherUpdate` in [ModuleOperationalRequirementTests](../../../../Tests/Core/Core.Tests/ModuleOperationalRequirementTests.cs) | Passed on Windows, 2026-10-26; passed on Windows, Arch Linux and RHEL-compatible CI run 40315088820 |
| Collectible contexts release their retired shadow directories | `RetiredShadowDirectory_IsReapedAfterItsContextIsCollected` in [ModuleOperationalRequirementTests](../../../../Tests/Core/Core.Tests/ModuleOperationalRequirementTests.cs) | Passed on Windows, 2026-10-26; passed on Windows, Arch Linux and RHEL-compatible CI run 40315088820 |
| Periodic reconciliation recovers from missed filesystem watcher events | `PeriodicReconciliation_RecoversWhenWatcherEventsAreMissed` in [ModuleOperationalRequirementTests](../../../../Tests/Core/Core.Tests/ModuleOperationalRequirementTests.cs) | Passed on Windows, 2026-10-26; passed on Windows, Arch Linux and RHEL-compatible CI run 40315088820 |
| Automatic reconciliation does not repeatedly restart an unchanged rejected package; explicit reload does | `AutomaticReconciliation_DoesNotRetryAnUnchangedRejectedPackage` in [ModuleOperationalRequirementTests](../../../../Tests/Core/Core.Tests/ModuleOperationalRequirementTests.cs) | Passed on Windows, 2026-10-26; passed on Windows, Arch Linux and RHEL-compatible CI run 40315088820 |
| Unsafe rollback withdrawal is visible as a restart-required package failure | `RollbackFailure_WithdrawsProvidersAndRejectsFurtherReloads` in [ModuleLifecycleTests](../../../../Tests/Core/Core.Tests/ModuleLifecycleTests.cs) | Passed on Windows, 2026-10-26; passed on Windows, Arch Linux and RHEL-compatible CI run 40315088820 |
| Package fingerprinting rejects file or directory links without traversing outside the package | `Fingerprint_RejectsDirectoryLinksWithoutTraversingThem` in [ModulePackageTests](../../../../Tests/Core/Core.Tests/ModulePackageTests.cs) | Test failed against the prior traversal, production traversal was corrected, then passed on Windows, 2026-10-26; passed on Windows, Arch Linux and RHEL-compatible CI run 40315088820 |

Review follow-up (2026-10-26, merged with the Node 24 CI action update):
the package manifest and fingerprint are now read from the same package snapshot
(`Inspect` fingerprints before and after reading the manifest); a newly loaded
package whose feature declarations do not match its registrations is treated as
a load failure so the running version is retained as last-known-good; and the
watcher callback tolerates timer disposal during shutdown. Covered by
`InvalidFeatureDeclarationUpdate_RetainsLastKnownGoodVersion` in
[ModuleLifecycleTests](../../../../Tests/Core/Core.Tests/ModuleLifecycleTests.cs).
The CI workflow actions were moved to Node 24 versions (`actions/checkout` v6,
`actions/upload-artifact` v7, `actions/setup-dotnet` v5).

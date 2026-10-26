# Poseidon

Modular maritime desktop application targeting .NET 10 and Avalonia on Windows,
Arch Linux, and RHEL. macOS is outside the supported scope. Development is
currently at the
reloadable-host foundation; chart reading/drawing, track management, navigation
calculations and device adapters are not implemented yet.

## Build and run the core application

Install the .NET 10 SDK and the platform dependencies required by Avalonia Desktop.
From the repository root:

```text
dotnet build Core.Application.proj -restore
dotnet run --project System/Core/Core/Core.csproj --no-build
```

Core remains the sole application executable. The build orchestration deploys
the eight core modules into the host output's `modules` directory; Core has no
project reference to those implementations. CoreUI supplies the normal window;
the host retains a small recovery window if CoreUI is missing or fails.
At startup, Core shows a borderless black splash with centered bold white
"Poseidon" text while discovering and loading modules. The splash closes when
initial loading completes; missing or failed CoreUI falls back to recovery.
CMS projects are not part of this build entry point.

For a published deployment, publish Core first, then stage the module packages
into its output:

```text
dotnet publish System/Core/Core/Core.csproj -o artifacts/host
dotnet build Core.Application.proj -restore -p:ModuleDeploymentDirectory=<absolute-publish-directory>/modules
```

Use the path syntax appropriate for your operating system. Do not ship the
reference datasets as part of the executable.

## Tests

```text
dotnet test Tests/Aggregate/Civilian.Tests/Civilian.Tests.csproj
```

The civilian aggregate executes all nine core module suites, including the
shared-contract tests, runtime integration tests, and headless Avalonia UI tests,
rather than only the aggregate smoke fact. Individual module projects can also
be tested independently. The existing military
aggregate/CMS scaffolding is deferred and is not the current validation gate.
Windows, Arch Linux, and RHEL are the target build/test environments. Windows
build, test, coverage, publish, and module-staging verification passed locally
on 2026-10-26. The first remote Arch Linux and RHEL-compatible container runs
and native Linux desktop smoke verification remain pending.

Collect production-code coverage using the existing Coverlet collector:

```text
dotnet test Tests/Aggregate/Civilian.Tests/Civilian.Tests.csproj --collect:"XPlat Code Coverage" --settings Tests/coverage.runsettings
```

Reports are written under each test project's ignored `TestResults` directory.
The coverage settings exclude test/fixture assemblies, not application code.
Headless UI tests exercise commands, status updates, close/restart behavior and
recovery-window visibility without requiring a display server. These complement,
but do not replace, real Windows/Arch Linux/RHEL desktop smoke tests.

## Continuous integration

The [CI workflow](.github/workflows/ci.yml) runs for pushes, pull requests, and
manual dispatches. It provides:

- a native GitHub-hosted Windows build, full solution test run, civilian
  coverage collection, and packaged application artifact;
- an Arch Linux container build, full solution test run, and packaged
  application artifact; and
- a Red Hat UBI 9 container build, full solution test run, and packaged
  application artifact as the initial RHEL-compatible gate.

The Linux container jobs use [.github/scripts/ci-linux.sh](.github/scripts/ci-linux.sh)
to install the .NET 10 SDK and distribution dependencies consistently. They
validate managed behavior, case-sensitive paths, packaging, and Linux runtime
dependencies, but they are not native-distribution GUI certification. Native
Arch Linux and RHEL runners with real desktop smoke tests remain an M1 gate.

## Module deployment and hot reload

Each package is a direct child directory of `<application-directory>/modules`:

```text
modules/
  Example/
    module.json
    Example.dll
    <private dependencies and resources>
    .ready
```

The build writes `.ready` last. For manual deployment, remove that marker, copy
the complete package, then restore the marker. Removing the entire directory
requests module removal. Incomplete replacement packages preserve the active
generation. The loader watches for changes, reconciles every five seconds, and
offers a manual rescan in the UI.

Example manifest:

```json
{
  "id": "Example",
  "version": "1.0.0",
  "entryAssembly": "Example.dll",
  "contractVersion": 1,
  "dependencies": [],
  "provides": [
    { "id": "example.panel", "version": "1.0.0", "cardinality": "multiple", "contract": "Poseidon.Core.Modules.IExamplePanel" }
  ],
  "contributions": [
    { "point": "shell.host", "id": "example.shell", "version": "1.0.0" },
    { "point": "example.panel", "id": "example.panel.clock", "version": "1.0.0" }
  ]
}
```

A package contains exactly one public concrete `IModule` implementation with a
public parameterless constructor. **`module.json` is the single source of
module identity**: the entry point does not restate its ID or version, so a
package can be renamed or re-versioned by editing only its manifest, and new
packages are discovered without host changes. The host names no module: it
never checks whether a particular module is loaded. A dependency version is a
*minimum within the same major version* (`1.2.0` is satisfied by `1.2.0`–`1.x`,
not `1.1.0` or `2.0.0`). Module IDs are plain strings chosen by the module author
(civilian modules use `Core.*`, e.g. `Core.Tracks`; `Core.Sonar` receives
civilian sonar input and is distinct from the military `CMS.Sonar`).

### Extension points and contributions

Manifests declare what a module changes. Unknown manifest fields are rejected.

- **`provides`** opens an *extension point* that other modules can extend. A
  point has an ID, a contract `version`, a `cardinality` (`single`: only one
  feature ID may target it; `multiple`: any number) and an optional `contract`
  (the full name of a shared interface every contribution must implement). The
  module that owns a concept declares its points; the host itself only owns
  `shell.host` (`ExtensionPoints.Shell`, contract `IShell`).
- **`contributions`** register features with a point. A contribution adds a new
  feature (UI such as a shell, chart layer or panel; or a service). Its `id` is
  the key the module registers in `IModule.Features`, and registrations must
  match the manifest exactly. `version` is the point version it targets (same
  major, point version at or above it).
- **Overrides** set `"replaces": true` on a contribution whose `id` matches a
  feature another module contributes, with the same version and a compatible
  contract. If several modules replace one feature, the highest `priority`
  wins; equal top priorities are ambiguous and all of them are excluded.
  Removing the replacement restores the original provider.

A contribution to a point that no loaded module (or the host) provides is
*Blocked*, and a module is started after the modules that provide the points it
contributes to and the originals it replaces. The host decides whether to show
its recovery window by asking whether any module contributes to `shell.host`.
The effective provider and target point of every feature are visible in
diagnostics.

Discovery and loading are fault-isolated per package. Every package directory
is reported in the UI with one state:

| State | Meaning |
|-------|---------|
| Active | Loaded and running. |
| Retained | A newer copy is invalid or incomplete; the last-known-good version keeps running. |
| Pending | A new package without its `.ready` marker; not a failure. |
| Failed | The package itself is invalid (manifest, assembly, duplicate ID, feature declarations, ambiguous override, start failure). |
| Blocked | The package is valid, but a dependency/replacement target is missing, incompatible, cyclic, or failed. |

Healthy packages keep loading alongside failed or blocked ones. Automatic
reconciliation does not retry an unchanged rejected package; changing the
package or using **Rescan / reload modules** retries it.

### Lifecycle rules and current limitations

- Modules implement the shared [contracts](System/Shared/Contracts/Modules.cs),
  not a reference to the Core executable. Constructors and feature registration
  must be side-effect free. `StartAsync` activates resources; `StopAsync` must
  be idempotent and also clean up a partially failed start.
- `CaptureStateAsync` returns versioned bytes; `StartAsync` must validate
  supplied state. Stop must release views, event subscriptions, tasks, timers,
  and file/device handles. Never cache plugin types in host-wide serializers
  or static registries; doing so can pin collectible assemblies.
- Reconciliation currently stages and replaces the **whole module generation**.
  This conservative approach also restarts unaffected modules; selective
  dependency-subgraph reload is future optimization.
- Feature calls go through `IModuleHost.InvokeAsync`, serialized with reloads.
  Calls must be short and synchronous; do not return plugin instances/delegates,
  retain providers, recursively invoke features, or invoke features from lifecycle
  methods. Richer asynchronous/streaming feature leases are not implemented yet.
- A ten-second lifecycle deadline bounds activation, state capture and stopping.
  A module that will not quiesce is reported as unsafe, and affected functionality
  becomes unavailable. The host never forcibly terminates plugin code. Such
  failures can require a process restart.
- Rejected candidates leave the prior instances running when safe; failed starts
  are excluded and the remaining plan is started (or the prior instances are
  restored if nothing else changed). Cleanup/rollback failure is explicit, not
  reported as successful recovery.
- Removing a required dependency blocks only its dependents; they are reported
  as Blocked and start automatically once the dependency returns.
- Packages load from per-instance shadow copies in collectible contexts, so
  deployed DLLs remain replaceable on Windows. Old-context collection is verified
  by tests; diagnostics expose contexts still awaiting collection. Shadow files
  are reaped on subsequent reconciliations once their contexts are collected.
- Contracts, Avalonia, and runtime infrastructure are host-owned and require a
  restart to upgrade. Native dependencies are rejected by the plugin context;
  use an isolated worker for native components that cannot be safely unloaded.
- Modules are trusted executable code, not sandboxed mods. Overrides are explicit
  shared-interface replacements, not arbitrary patching of host internals.

## Data and implementation memory

Initialize chart-test dependencies when needed:

```text
git submodule update --init --recursive
```

See the [application plan](Reference/Data/Plan-Memory/2026-10-24-poseidon-application-plan.md)
and [chart mappings](Reference/Data/Feature-Mappings/README.md) for ownership,
milestones and the remaining data-reader work.

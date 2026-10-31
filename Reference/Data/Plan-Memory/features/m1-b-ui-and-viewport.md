# M1-B: CoreUI and Display viewport boundary

Milestone: M1-B (prerequisite M1-A)
Status: Done. The pull request passed CI on Windows, Arch Linux and
RHEL-compatible containers and was merged (2026-10-26).

This file holds the UI and viewport material moved verbatim from the former
application plan: the UI and console interaction section (section 6, without the
CMS coordination proposal, which is in [cms-backlog.md](cms-backlog.md)), the
M1-B plan (section 18) and the M1-B ledger rows (section 17). Former section
numbers are kept in headings so older references remain traceable (see the
section map in the master plan).

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

## 18. M1-B: CoreUI/Display viewport boundary

Started 2026-10-26 after the M1-A automated gate passed. Requirements were
written as acceptance tests before production code (the test-first rule, now
part of [development-workflow.md](../development-workflow.md)).

### Design decisions (assumed; confirm or revise)

- **Display owns the point.** Display declares `display.viewport`
  (`single` cardinality, contract `IChartViewport`, version 1.0.0) and
  contributes `core.display.viewport` to it. A contribution to a point nobody
  provides blocks its whole package, so if CoreUI owned the point Display would
  be blocked without CoreUI, violating "CoreUI and Display must not depend on
  each other". A `single` point still allows a deliberate `replaces` override.
- **CoreUI discovers, never references.** CoreUI finds the effective
  contribution on `display.viewport` in `RuntimeStatus.Features` and obtains a
  view through `IModuleHost.InvokeAsync`. Feature calls are rejected inside
  lifecycle methods, so CoreUI attaches the viewport from its status refresh,
  not from `StartAsync`. It shows an explicit unavailable state when no
  provider exists or the provider fails, and does not retry a failed provider
  until the provider or generation changes (same policy as package retries).
- **Contracts stay UI-framework independent.** `IChartViewport.CreateView()`
  returns `object` (an Avalonia control created on the UI thread). This keeps
  `Poseidon.Contracts` free of Avalonia, which Charts, Tracks and Navigation
  also reference. A typed, separately shared UI-contract assembly would need
  loader and graph changes; reconsider it when typed UI contracts multiply.
- **View lifetime.** The view is the one sanctioned exception to "do not return
  plugin instances" from `InvokeAsync`: CoreUI owns the returned control and
  releases it on detach and on stop. Whole-generation reload stops CoreUI, so
  no window keeps a retired Display context alive.
- **Layer API scope.** Display keeps an ordered layer stack: a locked base
  layer (land and coastline) that cannot be hidden, and toggleable overlays
  stacked above it. Layer contribution by other modules, drawing primitives,
  the contour slider and preserving layer visibility across reload are M1-D
  work, specified with their first real layer producer.

### Requirements

| ID | Requirement |
|---|---|
| B1 | Display declares `display.viewport` backed by the shared `IChartViewport` contract and contributes its viewport to it |
| B2 | Display loads and is Active without CoreUI (no CoreUI/Display dependency in either direction) |
| B3 | Display's viewport starts with only the locked base layer; the base cannot be hidden; overlays stack above it and toggle; unknown or duplicate layer IDs are rejected |
| B4 | Display creates a new, empty view per request, with an explicit "no chart data" state, only on the UI thread |
| B5 | CoreUI hosts the effective viewport contribution next to its diagnostics |
| B6 | Without a viewport provider, CoreUI shows an explicit unavailable state and keeps diagnostics and recovery commands |
| B7 | CoreUI detaches the view when the provider disappears and attaches a replacement provider's view |
| B8 | A failing viewport provider is reported once in the UI and does not remove diagnostics |
| B9 | CoreUI releases the hosted view when stopped |
| B10 | With the real runtime, deployed CoreUI and Display packages show Display's viewport in the CoreUI window; removing Display and reloading falls back to the unavailable state without restarting Core |
| B11 | A module contributing to its own declared point is accepted and reported on that point |
| B12 | `Poseidon.Contracts` stays free of UI-framework references |

### Status

Implemented and verified on Windows (see the ledger rows B1 to B12 below).
M1-B is complete: the pull request passed CI on Windows, Arch Linux and
RHEL-compatible containers and was merged (2026-10-26). The M1-B exit criteria are met as
follows: CoreUI owns the normal UI and hosts the viewport (B5, B6); Display
contributes an empty viewport and layer API (B1, B3, B4); UI and Display reload
without restarting Core (B7, B10); diagnostics and recovery remain available
(B6, B8). Next: M1-C (chart specification/resource audit) and the M1-E Tracks
contracts, each starting with failing acceptance tests.

Later change by M1-E (see [m1-e-track-pipeline.md](m1-e-track-pipeline.md)):
the Display module itself adds a toggleable `display.tracks` overlay and a track
status line to its views. The viewport class still starts with only the locked
base layer (B3 is unchanged); the module adds the overlay when it is created.

## 17. Requirement-to-test ledger (M1-B rows)

| Requirement | Acceptance test or suite | Verification |
|---|---|---|
| M1-B B1: Display declares `display.viewport` backed by `IChartViewport` and contributes its viewport | `Manifest_DeclaresSingleViewportPointBackedByTheSharedContract` in [ViewportTests](../../../../Tests/Core/CoreDisplay.Tests/ViewportTests.cs) | Failed before implementation (missing behavior), then passed on Windows, 2026-10-26 |
| M1-B B2: Display is Active and provides the viewport without CoreUI | `Display_IsActiveAndProvidesTheViewportWithoutCoreUi` in [ShellViewportIntegrationTests](../../../../Tests/Core/CoreUI.Tests/ShellViewportIntegrationTests.cs) | Failed before implementation (missing behavior), then passed on Windows, 2026-10-26 |
| M1-B B3: Locked base layer; overlays stack above it and toggle; unknown, duplicate or second base layers are rejected | `Viewport_StartsWithOnlyTheLockedBaseLayer`, `BaseLayer_CannotBeHidden`, `Overlays_StackAboveTheBaseAndToggle`, `UnknownDuplicateOrSecondBaseLayers_AreRejected` in [ViewportTests](../../../../Tests/Core/CoreDisplay.Tests/ViewportTests.cs) | Failed before implementation (missing behavior), then passed on Windows, 2026-10-26 |
| M1-B B4: A new empty view per request with an explicit no-chart-data state, UI thread only | `CreateView_ReturnsANewEmptyViewWithAnExplicitNoChartDataState`, `CreateView_RejectsCallsOffTheUiThread` in [ViewportTests](../../../../Tests/Core/CoreDisplay.Tests/ViewportTests.cs) | Failed before implementation (missing behavior), then passed on Windows, 2026-10-26 |
| M1-B B5: CoreUI hosts the effective viewport next to diagnostics and requests it once | `HostsTheEffectiveViewportNextToDiagnosticsAndRequestsItOnce` in [ShellViewportTests](../../../../Tests/Core/CoreUI.Tests/ShellViewportTests.cs) | Failed before implementation (missing behavior), then passed on Windows, 2026-10-26 |
| M1-B B6: Without a provider CoreUI shows an unavailable state and keeps diagnostics and commands | `WithoutAViewportProvider_ShowsUnavailableStateAndKeepsDiagnosticsAndCommands` in [ShellViewportTests](../../../../Tests/Core/CoreUI.Tests/ShellViewportTests.cs) | Failed before implementation (missing behavior), then passed on Windows, 2026-10-26 |
| M1-B B7: CoreUI detaches a removed provider's view and attaches a replacement | `DetachesARemovedProviderAndAttachesItsReplacement` in [ShellViewportTests](../../../../Tests/Core/CoreUI.Tests/ShellViewportTests.cs) | Failed before implementation (missing behavior), then passed on Windows, 2026-10-26 |
| M1-B B8: A failing provider is reported once without removing diagnostics | `AFailingProviderIsReportedOnceWithoutRemovingDiagnostics` in [ShellViewportTests](../../../../Tests/Core/CoreUI.Tests/ShellViewportTests.cs) | Failed before implementation (missing behavior), then passed on Windows, 2026-10-26 |
| M1-B B9: CoreUI releases the hosted view when stopped | `Stop_ReleasesTheHostedView` in [ShellViewportTests](../../../../Tests/Core/CoreUI.Tests/ShellViewportTests.cs) | Failed before implementation (missing behavior), then passed on Windows, 2026-10-26 |
| M1-B B10: Real runtime shows Display's viewport in CoreUI and falls back after Display is removed, without restarting Core | `CoreUi_HostsDisplaysViewportAndFallsBackWhenDisplayIsRemoved` in [ShellViewportIntegrationTests](../../../../Tests/Core/CoreUI.Tests/ShellViewportIntegrationTests.cs) | Failed before implementation (missing behavior), then passed on Windows, 2026-10-26 |
| M1-B B11: A module contributing to its own declared point is Active and reported on that point | `ModuleContributingToItsOwnPoint_IsActiveAndReportedOnThatPoint` in [ModuleContributionTests](../../../../Tests/Core/Core.Tests/ModuleContributionTests.cs) | Guards existing runtime behavior: passed before and after, Windows, 2026-10-26 |
| M1-B B12: `Poseidon.Contracts` has no UI-framework reference | `Contracts_DoNotReferenceAUiFramework` in [ModuleContractTests](../../../../Tests/Shared/Contracts.Tests/ModuleContractTests.cs) | Guards existing design: passed before and after, Windows, 2026-10-26 |

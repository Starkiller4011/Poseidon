# M1-H: CoreUI extensibility, layout and input devices

Milestone: M1-H (new; depends on M1-B)
Status: Awaiting code approval

**This milestone is largely a refactor, not purely additive.** M1-B
delivered CoreUI's current hardcoded shell (`CoreUiModule.StartAsync`): one
`Grid` with a viewport host column and a side column of diagnostics
text/reload/exit, written directly in the module. M1-H replaces that
layout code wholesale with the page/panel/side-panel mechanism below, while
keeping Display's `IChartViewport` contract (B1 to B4, B12 in
[m1-b-ui-and-viewport.md](m1-b-ui-and-viewport.md)) and the viewport
hosting *behavior* (B5 to B9: discover, attach, detach, release) exactly as
they are. It is filed as its own milestone rather than more M1-B
requirement rows because the scope — layout across two hardware
configurations, a new Core-level input mechanism, and TID ownership — goes
well past "the CoreUI/Display viewport boundary" M1-B was scoped to.

## Problem and scope

Today CoreUI is a single hardcoded window: a fixed `Grid` with one viewport
host column and one side column (diagnostics text, a reload button, an
exit button, all written directly in `StartAsync`). Nothing else can add
UI to it, there is no concept of more than one screen or more than one
physical display, and the module gives the operator no input beyond
whatever Avalonia provides a window for free.

This milestone gives CoreUI three new owned responsibilities, continuing
the "CoreUI supplies the shell and panels" line from the master plan's UI
section (6):

1. **Layout, across two supported hardware configurations.** The chart is
   never paged away — it (and a track-information sidebar next to it) is
   always on screen. What *is* paged is a separate area for navigation and
   sensor control pages, and where that area lives depends on the
   hardware, chosen by a config file (not detected at runtime):
   - **Single display**: one window. Chart fills the main area; a sidebar
     holds track information on top and the paged control area on the
     bottom half.
   - **Dual display**: two windows. One shows only the chart and its
     track-information sidebar (never anything else); the other is given
     over entirely to the paged control area (sensor control, navigation,
     etc.).
   Both configurations have the same three input devices: keyboard,
   trackball, and a touchscreen (the TID, below). Other modules can
   contribute to the paged control area or the side-panel area, declaring
   which page their content belongs on (for paged content), and can
   register whole new pages of their own.
2. **Input devices.** The trackball needs no new abstraction — it presents
   to the OS as a standard pointing device, and Avalonia already gives
   every window pointer input for free. The keyboard does: Core itself
   (not CoreUI) gains a mechanism for any module to register a shortcut,
   scoped by an opaque "active scope" string (CoreUI's convention is the
   active control page's `PageId`), so the registry and conflict-checking
   do not belong to whichever module happens to own windows.
3. **TID (the touch panel used for buttonology).** A second surface CoreUI
   owns outright, independent of whether the main layout is single- or
   dual-display: a grid of soft buttons a module can register a page of,
   bound to that module's own actions. Switching the operator's active
   control page switches the TID's button layout to match.

Diagnostics (module/package status), reload and exit are **removed from
CoreUI's operator-facing shell entirely**, in both display modes. They
were only ever a development convenience; the shipped product does not
change modules or configuration often enough to need them in the main
console UI, and they belong instead with a future configuration-editing
tool (not planned here — see follow-ups) that will also own editing the
hardware-configuration file. Closing a window still requests an orderly
shutdown via the existing `host.RequestShutdown()` path; only the explicit
status/reload/exit panel is gone.

**Non-goals.** No physical trackball or touchscreen exists to test against
in CI; Avalonia's own pointer/tap simulation stands in, the same way every
other module's hardware-facing boundary is tested with recorded or
simulated input rather than real devices. TID fullscreen/kiosk mode (a
plain resizable, closable window is used for now; fullscreen on the real
touchscreen is explicit future follow-up once that hardware is selected),
keyboard-shortcut *discoverability UI*, TID hardware calibration and
physical button-grid legends, automatic monitor detection/assignment
(OS-level window placement, the same as the operator deciding where to
drag any other window — Poseidon does not manage it), the configuration-
editing tool itself, and multi-console/multi-operator support (CMS
backlog, not started) are all out of scope.

## Design

### Hardware configuration

CoreUI reads its own hardware-configuration file at startup — the same
precedent as CoreRadar's per-channel hardware configuration (R38 to R40 in
[m1-e-track-fusion.md](m1-e-track-fusion.md)): a module reads its own
structured config for a hardware detail the host's manifest has no reason
to know about. The file declares one setting for now:

```json
{ "displayMode": "Single" }
```

`Single` or `Dual` (case-sensitive, matching the existing hardware-config
file convention); anything else, or a missing/malformed file, is rejected
at startup with a clear diagnostic, the same as CoreRadar rejecting a
malformed channel file rather than guessing. There is no runtime monitor
detection: the file is the single source of truth for which layout to
build. This same file is the one a future configuration-editing tool will
expose for editing (not built here); new settings (such as TID fullscreen
mode) are expected to join it later as additive fields, not a reason to
add them now.

### Layout

**Chart region.** Unchanged from B5 to B9: CoreUI hosts the effective
`display.viewport` contribution exactly as today, released and
re-attached the same way. It is never part of the paging mechanism and is
always visible in both display modes.

**Side panels (e.g., track information).** A new, host-owned,
`Multiple`-cardinality point, `coreui.sidepanel` (contract `IUiSidePanel`):

```csharp
public interface IUiSidePanel
{
    int Order { get; }     // ties break on contribution ID, ordinal
    object CreateView();   // Avalonia control; UI thread only; new instance per call
}
```

Every contribution to this point is shown, stacked in `Order`, always next
to the chart region. Nothing about the mechanism is Tracks-specific —
"track information" is simply its first real consumer (the Tracks module
contributing a live summary view) — the same "other modules can add a UI
element" need applies here as much as it does to the paged control area
below.

**Control pages.** Its own region, entirely separate from the chart and
side panels:

```csharp
public interface IUiPage
{
    string PageId { get; }
    string DisplayName { get; }
}

public interface IUiPanel
{
    string PageId { get; }     // must name a registered IUiPage
    int Order { get; }         // ordering within the page; ties break on contribution ID, ordinal
    object CreateView();       // Avalonia control; UI thread only; new instance per call
}
```

`coreui.page` and `coreui.page.panel` are both `Multiple`-cardinality
points. As before, CoreUI itself enforces what a generic point cannot
express: at most one `IUiPage` may register a given `PageId` (a second is
rejected and reported); a panel naming an unregistered `PageId` is rejected
and reported. A page switcher lets the operator choose the active page;
switching detaches the previous page's panel views and attaches the new
page's (same release discipline as B9); removing a page's or panel's
provider while that page is active falls back to an explicit unavailable
state (B6/B7's pattern), without restarting Core.

**Where each region is hosted** depends only on the hardware-configuration
file, never on anything a module does:

- `Single`: one window — chart fills the main area; a sidebar holds side
  panels on top and the control-pages region (with its page switcher) on
  the bottom half.
- `Dual`: two windows — one holds only the chart and the side panels (no
  control-pages region at all); the other is entirely the control-pages
  region (with its page switcher), full window, no sidebar split.

Modules contributing side panels, pages or panels know nothing about which
mode is active or how many windows exist; they contribute to the same
points either way, and CoreUI's layout code decides where the content
physically ends up.

**No diagnostics/reload/exit panel.** CoreUI's shell shows only the
regions above. Window-close still calls `host.RequestShutdown()` (the
existing `Closing` handler behavior), so the operator can still close the
application from any window's native close control; there is simply no
dedicated status/reload/exit UI drawn inside it anymore.

### Keyboard shortcuts — a Core mechanism, not a CoreUI extension point

Unlike pages/panels/side panels/TID pages (all CoreUI-owned extension
points, since CoreUI owns the windows that host them), keyboard shortcuts
are a direct capability of `IModuleHost` itself — Core, not CoreUI — so the
registry and conflict-checking do not depend on which module happens to
own windows, and any module can register one without needing a manifest
contribution:

```csharp
public enum UiKey { /* a curated subset: letters, digits, F1-F12, arrows, Enter, Escape, etc. */ }

[Flags]
public enum UiKeyModifiers { None = 0, Shift = 1, Control = 2, Alt = 4 }

public interface IModuleHost
{
    // ...existing members (Status, RequestReload, RequestShutdown, InvokeAsync)...

    // scope is an opaque string the registering module and whichever module calls
    // SetActiveShortcutScope agree on by convention (CoreUI's convention is the active control
    // page's PageId); null means the shortcut fires regardless of the active scope. A gesture that
    // conflicts with an existing registration on overlapping scope (same scope, or either one null)
    // throws immediately, rather than silently overwriting it.
    IDisposable RegisterShortcut(UiKey key, UiKeyModifiers modifiers, string? scope, Action invoke);

    // Sets which scope's shortcuts DispatchShortcut currently reaches. CoreUI calls this whenever
    // the operator switches the active control page.
    void SetActiveShortcutScope(string? scope);

    // Called by whichever module owns the window(s) receiving raw keyboard input (CoreUI, for every
    // window it owns) once per key press. Invokes the registration matching the active scope, or a
    // global (null-scope) registration, and returns whether anything was invoked.
    bool DispatchShortcut(UiKey key, UiKeyModifiers modifiers);
}
```

`UiKey`/`UiKeyModifiers` stay framework-independent, the same reasoning as
`IChartViewport.CreateView` returning `object`: `Poseidon.Contracts` stays
free of Avalonia types (B12's existing rule). CoreUI maps them to the real
Avalonia gesture itself, the one place that translation happens, forwards
every window's raw key events to `DispatchShortcut`, and keeps
`SetActiveShortcutScope` in sync with the active control page's `PageId` —
that mapping from "scope" to "page" is a CoreUI convention Core itself
knows nothing about. A module disposes its registration handle (typically
in `StopAsync`) to remove it, so a reload cannot leave a stale or
duplicate registration behind.

The trackball itself gets no contract: it is a standard pointing device,
and every panel's Avalonia control already receives pointer input for
free, exactly like every other Avalonia control in the application.

### TID

CoreUI opens the TID as its own independent window, in both display modes,
with its own lifecycle (every window closes on `StopAsync`, the same
release discipline as B9). For this milestone it is a plain resizable,
closable window, same as the main shell — fullscreen/kiosk behavior on the
real touchscreen is explicit future follow-up, once that hardware is
selected, and is not attempted here. It shows a grid of soft buttons:

```csharp
public sealed record TidButton(string Id, string Label, bool IsEnabled = true);

public interface ITidPage
{
    string PageId { get; }     // links this TID layout to the control page of the same ID
    IReadOnlyList<TidButton> Buttons { get; }
    void Activate(string buttonId);
}
```

`coreui.tid.page` is a `Multiple`-cardinality point; CoreUI again enforces
at most one `ITidPage` per `PageId`, rejecting a second the same way. The
TID always shows the `ITidPage` whose `PageId` matches the control-pages
region's *currently active* page, so switching the active control page
switches the TID's buttons to match; if no `ITidPage` is registered for the
active page, the TID shows an explicit empty/unavailable state (the same
pattern as B6's unavailable viewport), never a blank or crashing window.
Tapping an enabled button calls `Activate(buttonId)` exactly once per tap;
a disabled button cannot be activated. Buttons are laid out in list order
for now; a fixed physical row/column grid (to match real button-box
hardware legends) is an additive follow-up once real TID hardware is
selected, not attempted here.

### Alternatives considered

- **Treating the chart as a selectable page.** Rejected by the user: the
  chart must always be visible, in both display modes, never one choice
  among several.
- **Auto-detecting single vs. dual display from connected monitors at
  runtime.** Rejected in favor of an explicit hardware-configuration file:
  monitor APIs are platform-specific, hot-plug behavior is untested, and
  the project already has a precedent (CoreRadar, R38 to R40) for a module
  reading its own hardware configuration file instead.
- **A CoreUI-owned `coreui.shortcut` extension point**, the same shape as
  pages/panels/TID pages. Rejected by the user in favor of a direct
  `IModuleHost` mechanism: shortcuts are a Core capability, independent of
  whichever module happens to own the windows that capture raw input, not
  something only CoreUI can offer.
- **Keeping diagnostics/reload/exit in CoreUI's shell.** Rejected by the
  user: they are development-time conveniences the shipped product does
  not need in its main console UI, and belong instead with a future
  configuration-editing tool.
- **Encoding page/placement metadata in the manifest's `Contribution`
  record** instead of on the contract interfaces. Rejected: the host is
  deliberately agnostic about what a contribution means (`Modules.cs`'s own
  comment: "the host assumes nothing about loaded modules"), and
  CoreRadar's per-channel configuration already established the pattern of
  a module reading its own structured data instead.

## Requirements

A fresh numbering (`H1` onward), since this is its own milestone rather
than a continuation of M1-B's `B`-numbering.

| ID | Requirement |
|---|---|
| H1 | CoreUI reads a hardware-configuration file at startup declaring `displayMode` (`Single` or `Dual`); a missing, malformed, or unrecognized value is rejected at startup with a clear diagnostic |
| H2 | The chart region (hosting the effective `display.viewport` contribution, per B5 to B9) is always visible in both display modes, never part of the paging mechanism |
| H3 | CoreUI declares `coreui.sidepanel` (`Multiple`, contract `IUiSidePanel`); every contribution is shown, stacked in `Order`, ties broken by contribution ID (ordinal), always next to the chart region |
| H4 | In `Single` display mode, one window holds the chart region, the side panels (top of the sidebar) and the control-pages region with its page switcher (bottom of the sidebar) |
| H5 | In `Dual` display mode, CoreUI opens two windows: one with only the chart region and the side panels; the other entirely the control-pages region with its page switcher |
| H6 | CoreUI declares `coreui.page` (`Multiple`, contract `IUiPage`) and `coreui.page.panel` (`Multiple`, contract `IUiPanel`) for the control-pages region |
| H7 | The control-pages region's page switcher lists every registered page by `DisplayName`; selecting one shows only that page's panels, shown in `Order`, ties broken by contribution ID (ordinal) |
| H8 | A panel naming an unregistered `PageId` is rejected and reported as a diagnostic, not silently dropped |
| H9 | A second `IUiPage` registered for a `PageId` already claimed is rejected and reported, rather than replacing or crashing |
| H10 | Switching the active control page detaches the previous page's panel views and attaches the new page's, releasing each the way B9 releases the viewport |
| H11 | Removing a control page's or panel's provider while that page is active falls back to an explicit unavailable state (B6/B7's pattern), without restarting Core |
| H12 | CoreUI's shell contains no diagnostics/status, reload or exit UI; closing any window still requests an orderly shutdown through the existing `host.RequestShutdown()` path |
| H13 | `IModuleHost.RegisterShortcut` returns an `IDisposable`; registering a gesture that conflicts with an existing registration on overlapping scope (same scope, or either one the global/null scope) throws immediately rather than silently overwriting it |
| H14 | `IModuleHost.SetActiveShortcutScope` plus `DispatchShortcut` invokes the registration matching the active scope, or a global (null-scope) registration, and does nothing for a gesture scoped to a different, inactive scope |
| H15 | Disposing a shortcut's registration handle removes it; a module's shortcuts are gone once it disposes its handles (e.g., during `StopAsync`), so a reload leaves no stale or duplicate registration |
| H16 | CoreUI forwards raw key events from every window it owns to `DispatchShortcut`, and calls `SetActiveShortcutScope` with the active control page's `PageId` whenever the operator switches control pages, in both display modes |
| H17 | CoreUI opens the TID as its own independent window — a plain resizable, closable window for this milestone — in both display modes, released the same way as every other window on `StopAsync` |
| H18 | CoreUI declares `coreui.tid.page` (`Multiple`, contract `ITidPage`) |
| H19 | The TID shows the `ITidPage` whose `PageId` matches the control-pages region's active page; switching the active control page switches the TID's buttons to match |
| H20 | No `ITidPage` registered for the active control page shows an explicit empty/unavailable TID state, not a blank or crashing window |
| H21 | A second `ITidPage` registered for a `PageId` already claimed is rejected and reported, the same conflict handling as H9 |
| H22 | Tapping an enabled button invokes that page's `Activate(buttonId)` exactly once per tap; a disabled button cannot be activated |
| H23 | Removing a TID page's provider while it is the active layout falls back to the empty TID state without restarting Core |

## Decisions

Confirmed by the user, 2026-11-01:

- The chart (and its side-panel area) is never a page; it is always
  visible in both display modes.
- Two supported hardware configurations (single/dual display), chosen by a
  config file, not detected at runtime.
- The TID is a plain resizable, closable window for this milestone;
  fullscreen/kiosk behavior on real touchscreen hardware is explicit
  future follow-up, not built now.
- Keyboard shortcuts are a mechanism Core (`IModuleHost`) itself provides,
  not a CoreUI-owned extension point; build it now even though it has no
  concrete first consumer yet (unlike pages/panels/side panels/TID, which
  all have one).
- This work is its own milestone (M1-H), not a continuation of M1-B's own
  requirement numbering, explicitly noting that a significant part of the
  implementation is a refactor of M1-B's existing CoreUI shell code (B5 to
  B9's behavior is preserved, but the code delivering it is rewritten).
- Diagnostics, reload and exit are removed from CoreUI's operator-facing
  shell entirely; they are development-time conveniences that belong with
  a future configuration-editing tool instead (not planned here).
- The hardware-configuration file mechanism is confirmed, and is expected
  to be exposed to that same future configuration-editing tool for
  editing.

## Open questions

None remaining. Ready for the Step 1 gate.

## Ledger

| Requirement | Test | Failing run (Step 2) | Passing run (Step 3) |
|---|---|---|---|
| H1 | `ConsoleHardwareConfigurationTests` (all cases), [CoreUI.Tests](../../../../Tests/Core/CoreUI.Tests/ConsoleHardwareConfigurationTests.cs) | `NotImplementedException` from the stub, 2026-11-01 | Passed, 2026-11-01 |
| H2 | `SidePanelTests.SidePanels_StayNextToTheChartRegion_WhichRemainsVisible`; `ShellLayoutIntegrationTests.SingleDisplayMode_...`/`DualDisplayMode_...` (chart-visible assertion) | Timed out waiting for side-panel content / for a second window, 2026-11-01 | Passed, 2026-11-01 |
| H3 | `SidePanelTests.SidePanels_AreShownStackedByOrder_TiesBrokenByContributionIdOrdinal` | Timed out; `coreui.sidepanel` is never read, 2026-11-01 | Passed, 2026-11-01 |
| H4 | `ShellLayoutIntegrationTests.SingleDisplayMode_OpensOneMainWindowPlusTheTid_AndTheChartRegionIsVisible` | Timed out; only one window ever opens, 2026-11-01 | Passed, 2026-11-01 |
| H5 | `ShellLayoutIntegrationTests.DualDisplayMode_OpensTwoMainWindowsPlusTheTid_OneWithOnlyTheChartAndSidePanels` | Timed out; only one window ever opens, 2026-11-01 | Passed, 2026-11-01 |
| H6 | `ControlPageTests.PageSwitcher_ListsEveryRegisteredPage_AndSelectingOneShowsOnlyItsPanels` | Timed out; `coreui.page`/`coreui.page.panel` are never read, 2026-11-01 | Passed, 2026-11-01 |
| H7 | `ControlPageTests.PageSwitcher_...`; `PanelsOnOnePage_AreShownStackedByOrder_TiesBrokenByContributionIdOrdinal` | Timed out, 2026-11-01 | Passed, 2026-11-01 |
| H8 | `ControlPageTests.PanelNamingAnUnregisteredPage_IsRejectedAndReportedAsADiagnostic` | Timed out waiting for `module.Diagnostics` (always empty stub), 2026-11-01 | Passed, 2026-11-01 |
| H9 | `ControlPageTests.SecondPageForAPageIdAlreadyClaimed_IsRejectedAndReported` | Timed out waiting for `module.Diagnostics`, 2026-11-01 | Passed, 2026-11-01 |
| H10 | `ControlPageTests.SwitchingPages_DetachesThePreviousPagesViews_AndAttachesTheNews` | Timed out, 2026-11-01 | Passed, 2026-11-01 |
| H11 | `ControlPageTests.RemovingTheActivePagesProvider_FallsBackToAnUnavailableState` | Timed out, 2026-11-01 | Passed, 2026-11-01 |
| H12 | `ShellChromeTests.Shell_HasNoDiagnosticsStatusReloadOrExitUi` (plus `ClosingAWindow_StillRequestsAnOrderlyShutdown`, which already passes — orderly shutdown on close is unchanged) | `Assert.DoesNotContain` found the old Rescan/Exit buttons still present, 2026-11-01 | Passed, 2026-11-01 |
| H13 | `KeyboardShortcutTests.RegisterShortcut_ReturnsADisposableHandle`, `ConflictingRegistration_OnOverlappingScope_ThrowsImmediately`, `NonOverlappingScopes_DoNotConflict`, [Core.Tests](../../../../Tests/Core/Core.Tests/KeyboardShortcutTests.cs) | `NotImplementedException` from the `IModuleHost` default, 2026-11-01 | Passed, 2026-11-01 |
| H14 | `KeyboardShortcutTests.Dispatch_InvokesTheRegistrationForTheActiveScope_AndReturnsTrue`, `Dispatch_DoesNothing_ForAGestureScopedToADifferentInactiveScope`, `Dispatch_InvokesAGlobalRegistration_RegardlessOfTheActiveScope` | `NotImplementedException`, 2026-11-01 | Passed, 2026-11-01 |
| H15 | `KeyboardShortcutTests.DisposingTheHandle_RemovesTheRegistration_SoAReloadLeavesNoStaleOrDuplicate` | `NotImplementedException`, 2026-11-01 | Passed, 2026-11-01 |
| H16 | `KeyboardForwardingTests.KeyPressOnTheMainWindow_IsForwardedToDispatchShortcut`, `SwitchingTheActiveControlPage_UpdatesTheHostsActiveShortcutScope`, [CoreUI.Tests](../../../../Tests/Core/CoreUI.Tests/KeyboardForwardingTests.cs) | `DispatchCalls` collection empty / scope never updates, 2026-11-01 | Passed, 2026-11-01 |
| H17 | `TidTests.Start_OpensASecondIndependentTidWindow`, `Stop_ClosesTheTidWindowToo` | Timed out; only one window ever opens, 2026-11-01 | Passed, 2026-11-01 |
| H18 | Every `TidTests` case (declares `coreui.tid.page`) | Timed out, 2026-11-01 | Passed, 2026-11-01 |
| H19 | `TidTests.TidShowsTheButtonsForTheActiveControlPage_AndSwitchesWithIt` | Timed out, 2026-11-01 | Passed, 2026-11-01 |
| H20 | `TidTests.WithNoTidPageRegisteredForTheActivePage_ShowsAnExplicitEmptyState` | Timed out, 2026-11-01 | Passed, 2026-11-01 |
| H21 | `TidTests.SecondTidPageForAPageIdAlreadyClaimed_IsRejectedAndReported` | Timed out waiting for `module.Diagnostics`, 2026-11-01 | Passed, 2026-11-01 |
| H22 | `TidTests.TidShowsTheButtonsForTheActiveControlPage_AndSwitchesWithIt` (tap), `DisabledButton_CannotBeActivated` | Timed out, 2026-11-01 | Passed, 2026-11-01 |
| H23 | `TidTests.RemovingTheActiveTidPagesProvider_FallsBackToTheEmptyState` | Timed out, 2026-11-01 | Passed, 2026-11-01 |

All [CoreUI.Tests](../../../../Tests/Core/CoreUI.Tests) (58 tests: 29 pre-existing/adjusted, now passing; 29 new, all failing as above) and all 7 new [Core.Tests](../../../../Tests/Core/Core.Tests/KeyboardShortcutTests.cs) cases were run on Windows, 2026-11-01, confirming every new test fails because the behavior does not exist yet, not because of how the test is set up. `dotnet build Poseidon.slnx`: no warnings or errors.

**Production code added this step is compile-only, with no behavior**, per the development-workflow rule: the `IUiSidePanel`/`IUiPage`/`IUiPanel`/`ITidPage`/`TidButton`/`CoreUiPoints` contracts and `UiKey`/`UiKeyModifiers` enums are real (interfaces and enums have no behavior to stub); `IModuleHost.RegisterShortcut`/`SetActiveShortcutScope`/`DispatchShortcut` default to `throw new NotImplementedException()` (so every existing `IModuleHost` test double keeps compiling unchanged — `ModuleRuntime` is the one real implementation Step 3 adds); `ConsoleHardwareConfiguration.Load` is the same kind of stub; CoreUI's `module.json` gained its real `provides` entries for the four new points (manifest data, not code); `CoreUiModule` gained one new test-only `internal` stub, `Diagnostics` (always empty), for H8/H9/H21's "rejected and reported" wording now that H12 removes the panel that used to show diagnostics text. `CoreUiModule.StartAsync` itself is untouched — every new test fails against the unchanged M1-B implementation, which is what let most of them fail without any production code changes at all.

### Step 3 (code), 2026-11-01

Implemented for real: `ConsoleHardwareConfiguration.Load` (JSON parsing,
same shape as `RadarHardwareConfiguration`); `ModuleRuntime`'s shortcut
registry (`RegisterShortcut`/`SetActiveShortcutScope`/`DispatchShortcut`,
with its own lock, independent of the module-reload machinery); and a full
rewrite of `CoreUiModule` — hardware-mode-driven window layout, the
always-visible chart and side-panel regions, the control-pages
region (page switcher, duplicate/orphan rejection, release discipline),
the TID window and its buttons, and keyboard forwarding. The chart
viewport's own hosting logic (B5 to B9) is untouched. As a simplification
(noted as a starting point, not a requirement gap): side panels and the
active control page's panels are rebuilt fresh on every 500ms tick rather
than gated on a (FeatureId, Generation) signature the way the single
viewport is — nothing here depends on `CreateView()` being called exactly
once, unlike the viewport's own tested `ViewportRequests == 1` guarantee.

**One real bug found while making the tests pass**, in
`RenderControlPagesContentAsync`: panels are shown by wrapping their views
in an intermediate `StackPanel` before assigning it to
`controlPagesHost.Content`. Replacing `.Content` detaches that
`StackPanel` from the window, but does not clear the `StackPanel`'s own
`Children` — so a panel's view keeps the (now-orphaned) `StackPanel` as
its `Parent` forever, failing `RemovingTheActivePagesProvider_FallsBackToAnUnavailableState`'s
release-discipline assertion deterministically (reproduced on every run,
confirmed with temporary tracing, not a timing race). Fixed by clearing
the previous `StackPanel`'s `Children` before replacing it.

**Three existing tests needed updating**, all because real new behavior
legitimately changed what they could assume (mechanical test-only fixes,
documented here per the development-workflow rule):
- `ShellLayoutIntegrationTests`'s two display-mode tests asserted
  `opened.Count(...) == N` and then, as a separate un-waited
  `Assert.Contains`, that the chart text had appeared — a real race, since
  the window count can satisfy the first check before the first 500ms
  refresh tick has populated the viewport placeholder. Fixed by folding
  both conditions into one `WaitUntil`.
- `ShellViewportIntegrationTests`'s `DeployedModules.Deploy` never wrote a
  `console.json` for the real CoreUI package it deploys; CoreUI now
  requires one to start at all (H1), so `AisInput_ReachesTheShellViewportThroughTracksAndDisplay`
  and `CoreUi_HostsDisplaysViewportAndFallsBackWhenDisplayIsRemoved` failed
  with the package Blocked. Fixed by writing a default `Single`-mode
  `console.json` alongside CoreUI's deployed files.
- The same two tests also asserted `Assert.Single(opened, w => w.IsVisible)`
  to find CoreUI's one window — no longer true now that CoreUI also opens
  the TID window (H17). Fixed by excluding the TID window
  (`!w.Title!.Contains("TID")`) from that lookup.

`FormatPackage` (and its two `CoreUIModuleTests` cases) was deleted: it
existed only to format the status panel H12 removes, and had no other
caller.

**Final passing run (Windows, 2026-11-01):** `dotnet build Poseidon.slnx`
and `dotnet build Core.Application.proj`: no warnings or errors.
`dotnet test Poseidon.slnx -maxCpuCount:1`: every project reports "Test Run
Successful" with zero failures, including `CoreUI.Tests` (55/55),
`Core.Tests` (123/123) and `Contracts.Tests` (94/94). `CoreUI.Tests` and
`Core.Tests` were also run standalone (several times, including while
chasing the `StackPanel` bug above) with the same zero-failure result.

**Existing tests revised because H12 removed behavior they asserted** (mechanical, documented here per the development-workflow rule, not repeated in the test files beyond a short comment): `ShellViewportTests.WithoutAViewportProvider_ShowsUnavailableStateAndKeepsDiagnosticsAndCommands` renamed to `WithoutAViewportProvider_ShowsUnavailableState` and its diagnostics/Rescan/Exit assertions dropped; `HostsTheEffectiveViewportNextToDiagnosticsAndRequestsItOnce` renamed to `HostsTheEffectiveViewportAndRequestsItOnce`, same change; `AFailingProviderIsReportedOnceWithoutRemovingDiagnostics` renamed to `AFailingProviderIsReportedOnce`, same change; `UiLifecycleTests.Start_ShowsStatusAndBindsReloadExitCommands` renamed to `Start_ShowsGenerationInTheWindowTitle`, trimmed to just the still-valid window-title assertion. The underlying B5 to B9 viewport-hosting behavior these tests cover is otherwise unchanged and still passes.

## Status and follow-ups

Step 1 (plan) and Step 2 (tests) approved. Step 3 (code) done: every
requirement H1 to H23 is implemented and its test passes; the full
solution (`dotnet test Poseidon.slnx -maxCpuCount:1`) is green with zero
failures, 2026-11-01. Awaiting the user's acceptance to close out M1-H.
Known follow-ups, explicitly out of scope for this milestone:

- A fixed physical row/column TID button grid matching real hardware
  legends, once real TID hardware is selected.
- TID fullscreen/kiosk window behavior, once real TID hardware is
  selected.
- A configuration-editing tool (not yet planned) to own module/package
  diagnostics, reload, exit, and editing the hardware-configuration file.
- Keyboard-shortcut discoverability UI (a help overlay, printed legend,
  etc.).
- Side panels and the active control page's panels are rebuilt (and their
  `CreateView()` called) on every 500ms refresh tick rather than gated on
  a (FeatureId, Generation) signature the way the chart viewport is.
  Revisit if a real panel's `CreateView()` ever turns out to be expensive
  or stateful in a way that matters.
- Multi-console/multi-operator support (CMS backlog, not started).
- Runtime monitor detection/assignment (OS-level window placement stays
  the operator's job, not Poseidon's).

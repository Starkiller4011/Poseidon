namespace Poseidon.Core.Modules;

// Points declared by CoreUI (M1-H, m1-h-ui-extensibility.md). CoreUI owns the shell's layout and is the
// only consumer of these; other modules contribute to them without knowing how many windows exist or
// which hardware configuration (console.json's displayMode) is active.
public static class CoreUiPoints
{
    // Always visible next to the chart region, regardless of display mode (H3).
    public const string SidePanel = "coreui.sidepanel";
    // Registers a page for the control-pages region (H6).
    public const string Page = "coreui.page";
    // Contributes content to a page registered on CoreUiPoints.Page (H6).
    public const string PagePanel = "coreui.page.panel";
    // Registers a TID (touch panel) button layout for a page of the same ID (H18).
    public const string TidPage = "coreui.tid.page";
}

// H3: always shown, stacked in Order, next to the chart region. Not specific to any one module's
// content (track information is simply its first real consumer).
public interface IUiSidePanel
{
    int Order { get; }
    object CreateView();
}

// H6: registers a page for the control-pages region; PageId must be unique across every IUiPage
// contribution (H9).
public interface IUiPage
{
    string PageId { get; }
    string DisplayName { get; }
}

// H6: contributes content to a page registered via IUiPage. PageId must name a registered page (H8);
// panels on the same page are shown stacked in Order.
public interface IUiPanel
{
    string PageId { get; }
    int Order { get; }
    object CreateView();
}

// H18 to H23: one soft button on the TID (the touch panel used for buttonology).
public sealed record TidButton(string Id, string Label, bool IsEnabled = true);

// H18: registers the TID's button layout for the control page of the same PageId; PageId must be
// unique across every ITidPage contribution (H21).
public interface ITidPage
{
    string PageId { get; }
    IReadOnlyList<TidButton> Buttons { get; }
    void Activate(string buttonId);
}

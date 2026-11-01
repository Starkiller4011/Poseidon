using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Poseidon.Core.Modules;
using Poseidon.Modules.CoreUI;
using Xunit;
using static CoreUI.Tests.ShellViewportTests;

namespace CoreUI.Tests;

// H3: coreui.sidepanel does not exist yet (CoreUiModule never reads it), so every test below fails,
// uncaught, because the contributed view never appears.
public sealed class SidePanelTests
{
    [AvaloniaFact]
    public async Task SidePanels_AreShownStackedByOrder_TiesBrokenByContributionIdOrdinal()
    {
        var host = new MultiFeatureHost();
        var first = new TextBlock { Text = "first side panel" };
        var second = new TextBlock { Text = "second side panel" };
        var third = new TextBlock { Text = "third side panel" };
        // Deliberately provided out of Order, and with contribution IDs that sort differently than
        // Order would alone, so the test cannot pass by coincidence of provide-call order. Order 0
        // (a.panel) comes first; the order-1 tie between b.panel and z.panel breaks on contribution ID
        // ordinal ("b.panel" < "z.panel"), so third (b.panel) comes before second (z.panel).
        host.Provide(CoreUiPoints.SidePanel, "z.panel", "Z", new FakeSidePanel(1, () => second));
        host.Provide(CoreUiPoints.SidePanel, "a.panel", "A", new FakeSidePanel(0, () => first));
        host.Provide(CoreUiPoints.SidePanel, "b.panel", "B", new FakeSidePanel(1, () => third));
        var module = new CoreUiModule();
        try
        {
            await module.StartAsync(host, null, CancellationToken.None);
            var window = Assert.IsType<Window>(module.ActiveWindow);
            await WaitUntil(() => window.GetVisualDescendants().Contains(first) &&
                                   window.GetVisualDescendants().Contains(second) &&
                                   window.GetVisualDescendants().Contains(third));
            var order = window.GetVisualDescendants()
                .Where(v => v == first || v == second || v == third)
                .ToList();
            Assert.Equal([first, third, second], order);
        }
        finally { await module.StopAsync(CancellationToken.None); }
    }

    [AvaloniaFact]
    public async Task SidePanels_StayNextToTheChartRegion_WhichRemainsVisible()
    {
        // H2: the chart region is never displaced by side panels.
        var host = new MultiFeatureHost();
        var view = new TextBlock { Text = "vendor viewport view" };
        host.Provide(DisplayPoints.Viewport, "vendor.viewport", "Vendor", new FakeViewport(() => view));
        host.Provide(CoreUiPoints.SidePanel, "tracks.sidepanel", "Core.Tracks",
            new FakeSidePanel(0, () => new TextBlock { Text = "track information" }));
        var module = new CoreUiModule();
        try
        {
            await module.StartAsync(host, null, CancellationToken.None);
            var window = Assert.IsType<Window>(module.ActiveWindow);
            await WaitUntil(() => window.GetVisualDescendants().Contains(view));
            Assert.Contains("track information", Text(window));
        }
        finally { await module.StopAsync(CancellationToken.None); }
    }

    private sealed class FakeSidePanel(int order, Func<object> create) : IUiSidePanel
    {
        public int Order => order;
        public object CreateView() => create();
    }
}

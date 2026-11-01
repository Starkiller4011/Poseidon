using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Poseidon.Core.Modules;
using Poseidon.Modules.CoreUI;
using Xunit;
using static CoreUI.Tests.ShellViewportTests;

namespace CoreUI.Tests;

// H6 to H11: coreui.page/coreui.page.panel do not exist yet (CoreUiModule never reads them), so every
// test below fails, uncaught, because the expected page switcher or panel content never appears.
public sealed class ControlPageTests
{
    [AvaloniaFact]
    public async Task PageSwitcher_ListsEveryRegisteredPage_AndSelectingOneShowsOnlyItsPanels()
    {
        var host = new MultiFeatureHost();
        host.Provide(CoreUiPoints.Page, "nav.page", "Core.Navigation", new FakePage("navigation", "Navigation"));
        host.Provide(CoreUiPoints.Page, "sensors.page", "Core.Radar", new FakePage("sensors", "Sensors"));
        host.Provide(CoreUiPoints.PagePanel, "nav.panel", "Core.Navigation",
            new FakePanel("navigation", 0, () => new TextBlock { Text = "navigation controls" }));
        host.Provide(CoreUiPoints.PagePanel, "sensors.panel", "Core.Radar",
            new FakePanel("sensors", 0, () => new TextBlock { Text = "sensor controls" }));
        var module = new CoreUiModule();
        try
        {
            await module.StartAsync(host, null, CancellationToken.None);
            var window = Assert.IsType<Window>(module.ActiveWindow);
            await WaitUntil(() => Text(window).Contains("Navigation") && Text(window).Contains("Sensors"));

            SelectPage(window, "Sensors");
            await WaitUntil(() => Text(window).Contains("sensor controls"));
            Assert.DoesNotContain("navigation controls", Text(window));

            SelectPage(window, "Navigation");
            await WaitUntil(() => Text(window).Contains("navigation controls"));
            Assert.DoesNotContain("sensor controls", Text(window));
        }
        finally { await module.StopAsync(CancellationToken.None); }
    }

    [AvaloniaFact]
    public async Task PanelsOnOnePage_AreShownStackedByOrder_TiesBrokenByContributionIdOrdinal()
    {
        var host = new MultiFeatureHost();
        host.Provide(CoreUiPoints.Page, "sensors.page", "Core.Radar", new FakePage("sensors", "Sensors"));
        var first = new TextBlock { Text = "first panel" };
        var second = new TextBlock { Text = "second panel" };
        host.Provide(CoreUiPoints.PagePanel, "b.panel", "B", new FakePanel("sensors", 0, () => first));
        host.Provide(CoreUiPoints.PagePanel, "a.panel", "A", new FakePanel("sensors", 0, () => second));
        var module = new CoreUiModule();
        try
        {
            await module.StartAsync(host, null, CancellationToken.None);
            var window = Assert.IsType<Window>(module.ActiveWindow);
            await WaitUntil(() => window.GetVisualDescendants().Contains(first) &&
                                   window.GetVisualDescendants().Contains(second));
            var order = window.GetVisualDescendants().Where(v => v == first || v == second).ToList();
            Assert.Equal([second, first], order);
        }
        finally { await module.StopAsync(CancellationToken.None); }
    }

    [AvaloniaFact]
    public async Task PanelNamingAnUnregisteredPage_IsRejectedAndReportedAsADiagnostic()
    {
        var host = new MultiFeatureHost();
        host.Provide(CoreUiPoints.PagePanel, "orphan.panel", "Vendor",
            new FakePanel("no-such-page", 0, () => new TextBlock { Text = "orphan content" }));
        var module = new CoreUiModule();
        try
        {
            await module.StartAsync(host, null, CancellationToken.None);
            var window = Assert.IsType<Window>(module.ActiveWindow);
            await WaitUntil(() => module.Diagnostics.Any(d => d.Contains("orphan.panel")));
            Assert.DoesNotContain("orphan content", Text(window));
        }
        finally { await module.StopAsync(CancellationToken.None); }
    }

    [AvaloniaFact]
    public async Task SecondPageForAPageIdAlreadyClaimed_IsRejectedAndReported()
    {
        var host = new MultiFeatureHost();
        host.Provide(CoreUiPoints.Page, "first.page", "Vendor.One", new FakePage("sensors", "Sensors (one)"));
        host.Provide(CoreUiPoints.Page, "second.page", "Vendor.Two", new FakePage("sensors", "Sensors (two)"));
        var module = new CoreUiModule();
        try
        {
            await module.StartAsync(host, null, CancellationToken.None);
            var window = Assert.IsType<Window>(module.ActiveWindow);
            await WaitUntil(() => module.Diagnostics.Any(d => d.Contains("second.page")));
            Assert.DoesNotContain("Sensors (two)", Text(window));
        }
        finally { await module.StopAsync(CancellationToken.None); }
    }

    [AvaloniaFact]
    public async Task SwitchingPages_DetachesThePreviousPagesViews_AndAttachesTheNews()
    {
        var host = new MultiFeatureHost();
        host.Provide(CoreUiPoints.Page, "nav.page", "Core.Navigation", new FakePage("navigation", "Navigation"));
        host.Provide(CoreUiPoints.Page, "sensors.page", "Core.Radar", new FakePage("sensors", "Sensors"));
        var navView = new TextBlock { Text = "navigation controls" };
        host.Provide(CoreUiPoints.PagePanel, "nav.panel", "Core.Navigation", new FakePanel("navigation", 0, () => navView));
        var module = new CoreUiModule();
        try
        {
            await module.StartAsync(host, null, CancellationToken.None);
            var window = Assert.IsType<Window>(module.ActiveWindow);
            await WaitUntil(() => window.GetVisualDescendants().Contains(navView));

            SelectPage(window, "Sensors");
            await WaitUntil(() => navView.Parent is null);
        }
        finally { await module.StopAsync(CancellationToken.None); }
    }

    [AvaloniaFact]
    public async Task RemovingTheActivePagesProvider_FallsBackToAnUnavailableState()
    {
        var host = new MultiFeatureHost();
        host.Provide(CoreUiPoints.Page, "sensors.page", "Core.Radar", new FakePage("sensors", "Sensors"));
        var view = new TextBlock { Text = "sensor controls" };
        host.Provide(CoreUiPoints.PagePanel, "sensors.panel", "Core.Radar", new FakePanel("sensors", 0, () => view));
        var module = new CoreUiModule();
        try
        {
            await module.StartAsync(host, null, CancellationToken.None);
            var window = Assert.IsType<Window>(module.ActiveWindow);
            await WaitUntil(() => window.GetVisualDescendants().Contains(view));

            host.Withdraw("sensors.page");
            host.Withdraw("sensors.panel");
            await WaitUntil(() => Text(window).Contains("No control page is available."));
            Assert.Null(view.Parent);
        }
        finally { await module.StopAsync(CancellationToken.None); }
    }

    private static void SelectPage(Window window, string displayName)
        => Assert.Single(window.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, displayName))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private sealed class FakePage(string pageId, string displayName) : IUiPage
    {
        public string PageId => pageId;
        public string DisplayName => displayName;
    }

    private sealed class FakePanel(string pageId, int order, Func<object> create) : IUiPanel
    {
        public string PageId => pageId;
        public int Order => order;
        public object CreateView() => create();
    }
}

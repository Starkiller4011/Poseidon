using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Poseidon.Core.Modules;
using Poseidon.Modules.CoreUI;
using Xunit;
using static CoreUI.Tests.ShellViewportTests;

namespace CoreUI.Tests;

// H17 to H23: CoreUI does not open a TID window or read coreui.tid.page yet, so every test below fails,
// uncaught, because the expected second window or button content never appears.
public sealed class TidTests
{
    private static List<Window> TrackOpenedWindows(out IDisposable subscription)
    {
        var opened = new List<Window>();
        subscription = Window.WindowOpenedEvent.AddClassHandler(typeof(Window),
            (sender, _) => opened.Add((Window)sender!), Avalonia.Interactivity.RoutingStrategies.Direct);
        return opened;
    }

    [AvaloniaFact]
    public async Task Start_OpensASecondIndependentTidWindow()
    {
        var opened = TrackOpenedWindows(out var subscription);
        using var _ = subscription;
        var host = new MultiFeatureHost();
        var module = new CoreUiModule();
        try
        {
            await module.StartAsync(host, null, CancellationToken.None);
            await WaitUntil(() => opened.Count(w => w.IsVisible) >= 2);
            Assert.Contains(opened, w => w.IsVisible && w != module.ActiveWindow);
        }
        finally { await module.StopAsync(CancellationToken.None); }
    }

    [AvaloniaFact]
    public async Task Stop_ClosesTheTidWindowToo()
    {
        var opened = TrackOpenedWindows(out var subscription);
        using var _ = subscription;
        var host = new MultiFeatureHost();
        var module = new CoreUiModule();
        await module.StartAsync(host, null, CancellationToken.None);
        await WaitUntil(() => opened.Count(w => w.IsVisible) >= 2);
        await module.StopAsync(CancellationToken.None);
        Assert.All(opened, w => Assert.False(w.IsVisible));
    }

    [AvaloniaFact]
    public async Task WithNoTidPageRegisteredForTheActivePage_ShowsAnExplicitEmptyState()
    {
        var opened = TrackOpenedWindows(out var subscription);
        using var _ = subscription;
        var host = new MultiFeatureHost();
        var module = new CoreUiModule();
        try
        {
            await module.StartAsync(host, null, CancellationToken.None);
            await WaitUntil(() => opened.Count(w => w.IsVisible) >= 2);
            var tid = Assert.Single(opened, w => w.IsVisible && w != module.ActiveWindow);
            await WaitUntil(() => Text(tid).Contains("No buttons are available."));
        }
        finally { await module.StopAsync(CancellationToken.None); }
    }

    [AvaloniaFact]
    public async Task TidShowsTheButtonsForTheActiveControlPage_AndSwitchesWithIt()
    {
        var opened = TrackOpenedWindows(out var subscription);
        using var _ = subscription;
        var host = new MultiFeatureHost();
        host.Provide(CoreUiPoints.Page, "nav.page", "Core.Navigation", new FakePage("navigation", "Navigation"));
        host.Provide(CoreUiPoints.Page, "sensors.page", "Core.Radar", new FakePage("sensors", "Sensors"));
        var navActivated = new List<string>();
        host.Provide(CoreUiPoints.TidPage, "nav.tid", "Core.Navigation",
            new FakeTidPage("navigation", [new TidButton("go", "Go To")], navActivated.Add));
        var sensorsActivated = new List<string>();
        host.Provide(CoreUiPoints.TidPage, "sensors.tid", "Core.Radar",
            new FakeTidPage("sensors", [new TidButton("standby", "Standby")], sensorsActivated.Add));
        var module = new CoreUiModule();
        try
        {
            await module.StartAsync(host, null, CancellationToken.None);
            await WaitUntil(() => opened.Count(w => w.IsVisible) >= 2);
            var tid = Assert.Single(opened, w => w.IsVisible && w != module.ActiveWindow);
            var main = Assert.IsType<Window>(module.ActiveWindow);

            await WaitUntil(() => Text(tid).Contains("Go To"));
            Assert.DoesNotContain("Standby", Text(tid));
            Tap(tid, "Go To");
            Assert.Equal(["go"], navActivated);

            Assert.Single(main.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, "Sensors"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => Text(tid).Contains("Standby"));
            Assert.DoesNotContain("Go To", Text(tid));
        }
        finally { await module.StopAsync(CancellationToken.None); }
    }

    [AvaloniaFact]
    public async Task DisabledButton_CannotBeActivated()
    {
        var opened = TrackOpenedWindows(out var subscription);
        using var _ = subscription;
        var host = new MultiFeatureHost();
        host.Provide(CoreUiPoints.Page, "nav.page", "Core.Navigation", new FakePage("navigation", "Navigation"));
        var activated = new List<string>();
        host.Provide(CoreUiPoints.TidPage, "nav.tid", "Core.Navigation",
            new FakeTidPage("navigation", [new TidButton("go", "Go To", IsEnabled: false)], activated.Add));
        var module = new CoreUiModule();
        try
        {
            await module.StartAsync(host, null, CancellationToken.None);
            await WaitUntil(() => opened.Count(w => w.IsVisible) >= 2);
            var tid = Assert.Single(opened, w => w.IsVisible && w != module.ActiveWindow);
            await WaitUntil(() => Text(tid).Contains("Go To"));
            Tap(tid, "Go To");
            Assert.Empty(activated);
        }
        finally { await module.StopAsync(CancellationToken.None); }
    }

    [AvaloniaFact]
    public async Task SecondTidPageForAPageIdAlreadyClaimed_IsRejectedAndReported()
    {
        var host = new MultiFeatureHost();
        host.Provide(CoreUiPoints.Page, "nav.page", "Core.Navigation", new FakePage("navigation", "Navigation"));
        host.Provide(CoreUiPoints.TidPage, "first.tid", "Vendor.One",
            new FakeTidPage("navigation", [new TidButton("a", "A")], _ => { }));
        host.Provide(CoreUiPoints.TidPage, "second.tid", "Vendor.Two",
            new FakeTidPage("navigation", [new TidButton("b", "B")], _ => { }));
        var module = new CoreUiModule();
        try
        {
            await module.StartAsync(host, null, CancellationToken.None);
            await WaitUntil(() => module.Diagnostics.Any(d => d.Contains("second.tid")));
        }
        finally { await module.StopAsync(CancellationToken.None); }
    }

    [AvaloniaFact]
    public async Task RemovingTheActiveTidPagesProvider_FallsBackToTheEmptyState()
    {
        var opened = TrackOpenedWindows(out var subscription);
        using var _ = subscription;
        var host = new MultiFeatureHost();
        host.Provide(CoreUiPoints.Page, "nav.page", "Core.Navigation", new FakePage("navigation", "Navigation"));
        host.Provide(CoreUiPoints.TidPage, "nav.tid", "Core.Navigation",
            new FakeTidPage("navigation", [new TidButton("go", "Go To")], _ => { }));
        var module = new CoreUiModule();
        try
        {
            await module.StartAsync(host, null, CancellationToken.None);
            await WaitUntil(() => opened.Count(w => w.IsVisible) >= 2);
            var tid = Assert.Single(opened, w => w.IsVisible && w != module.ActiveWindow);
            await WaitUntil(() => Text(tid).Contains("Go To"));

            host.Withdraw("nav.tid");
            await WaitUntil(() => Text(tid).Contains("No buttons are available."));
        }
        finally { await module.StopAsync(CancellationToken.None); }
    }

    private static void Tap(Window window, string label)
        => Assert.Single(window.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, label))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private sealed class FakePage(string pageId, string displayName) : IUiPage
    {
        public string PageId => pageId;
        public string DisplayName => displayName;
    }

    private sealed class FakeTidPage(string pageId, IReadOnlyList<TidButton> buttons, Action<string> activate) : ITidPage
    {
        public string PageId => pageId;
        public IReadOnlyList<TidButton> Buttons => buttons;
        public void Activate(string buttonId) => activate(buttonId);
    }
}

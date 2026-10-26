using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Poseidon.Core.Modules;
using Poseidon.Modules.CoreUI;
using Xunit;

namespace CoreUI.Tests;

public sealed class ShellViewportTests
{
    private const string Unavailable = "Chart viewport unavailable";

    [AvaloniaFact]
    public async Task WithoutAViewportProvider_ShowsUnavailableStateAndKeepsDiagnosticsAndCommands()
    {
        var host = new ViewportHost();
        var module = new CoreUiModule();
        try
        {
            await module.StartAsync(host, null, CancellationToken.None);
            var window = Assert.IsType<Window>(module.ActiveWindow);
            await WaitUntil(() => Text(window).Contains(Unavailable));
            Assert.Contains("example diagnostic", Text(window));
            Assert.Contains(window.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, "Rescan / reload modules"));
            Assert.Contains(window.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, "Exit"));
            Assert.Equal(0, host.ViewportRequests);
        }
        finally { await module.StopAsync(CancellationToken.None); }
    }

    [AvaloniaFact]
    public async Task HostsTheEffectiveViewportNextToDiagnosticsAndRequestsItOnce()
    {
        var host = new ViewportHost();
        var view = new TextBlock { Text = "vendor viewport view" };
        host.Provide("vendor.viewport", new FakeViewport(() => view));
        var module = new CoreUiModule();
        try
        {
            await module.StartAsync(host, null, CancellationToken.None);
            var window = Assert.IsType<Window>(module.ActiveWindow);
            await WaitUntil(() => window.GetVisualDescendants().Contains(view));
            Assert.Contains("example diagnostic", Text(window));
            Assert.DoesNotContain(Unavailable, Text(window));
            await Task.Delay(1100);
            Assert.Equal(1, host.ViewportRequests);
        }
        finally { await module.StopAsync(CancellationToken.None); }
    }

    [AvaloniaFact]
    public async Task DetachesARemovedProviderAndAttachesItsReplacement()
    {
        var host = new ViewportHost();
        var original = new TextBlock { Text = "original view" };
        host.Provide("display.one", new FakeViewport(() => original));
        var module = new CoreUiModule();
        try
        {
            await module.StartAsync(host, null, CancellationToken.None);
            var window = Assert.IsType<Window>(module.ActiveWindow);
            await WaitUntil(() => window.GetVisualDescendants().Contains(original));

            host.Withdraw();
            await WaitUntil(() => Text(window).Contains(Unavailable));
            Assert.Null(original.Parent);

            var replacement = new TextBlock { Text = "replacement view" };
            host.Provide("display.two", new FakeViewport(() => replacement), generation: 5);
            await WaitUntil(() => window.GetVisualDescendants().Contains(replacement));
            Assert.DoesNotContain(Unavailable, Text(window));
            Assert.Null(original.Parent);
        }
        finally { await module.StopAsync(CancellationToken.None); }
    }

    [AvaloniaFact]
    public async Task AFailingProviderIsReportedOnceWithoutRemovingDiagnostics()
    {
        var host = new ViewportHost();
        host.Provide("broken.viewport", new FakeViewport(() => throw new InvalidOperationException("viewport exploded")));
        var module = new CoreUiModule();
        try
        {
            await module.StartAsync(host, null, CancellationToken.None);
            var window = Assert.IsType<Window>(module.ActiveWindow);
            await WaitUntil(() => Text(window).Contains("viewport exploded"));
            Assert.Contains(Unavailable, Text(window));
            Assert.Contains("example diagnostic", Text(window));
            await Task.Delay(1100);
            Assert.Equal(1, host.ViewportRequests);
        }
        finally { await module.StopAsync(CancellationToken.None); }
    }

    [AvaloniaFact]
    public async Task Stop_ReleasesTheHostedView()
    {
        var host = new ViewportHost();
        var view = new TextBlock { Text = "hosted view" };
        host.Provide("display.viewport.feature", new FakeViewport(() => view));
        var module = new CoreUiModule();
        try
        {
            await module.StartAsync(host, null, CancellationToken.None);
            var window = Assert.IsType<Window>(module.ActiveWindow);
            await WaitUntil(() => window.GetVisualDescendants().Contains(view));
            await module.StopAsync(CancellationToken.None);
            Assert.Null(view.Parent);
        }
        finally { await module.StopAsync(CancellationToken.None); }
    }

    internal static string Text(Window window)
        => string.Join("\n", window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));

    internal static async Task WaitUntil(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
            await Task.Delay(50, deadline.Token);
    }

    private sealed class FakeViewport(Func<object> create) : IChartViewport
    {
        public object CreateView() => create();
        public IReadOnlyList<ViewportLayer> Layers => [];
        public void SetLayerVisibility(string layerId, bool visible) => throw new NotSupportedException();
    }

    private sealed class ViewportHost : IModuleHost
    {
        private IChartViewport? viewport;
        public RuntimeStatus Status { get; private set; } = new(3,
            [new("example.module", "1.0.0", "package")],
            [new("example.feature", "example.module")], ["example diagnostic"]);
        public int ViewportRequests { get; private set; }

        public void Provide(string featureId, IChartViewport provider, long? generation = null)
        {
            viewport = provider;
            Status = Status with
            {
                Generation = generation ?? Status.Generation,
                Features = [new("example.feature", "example.module"),
                    new(featureId, "Vendor.Display", DisplayPoints.Viewport)]
            };
        }

        public void Withdraw()
        {
            viewport = null;
            Status = Status with { Features = [new("example.feature", "example.module")] };
        }

        public void RequestReload() { }
        public void RequestShutdown() { }

        public Task<TResult> InvokeAsync<TFeature, TResult>(string featureId, Func<TFeature, TResult> action,
            CancellationToken cancellationToken = default) where TFeature : class
        {
            if (viewport is not TFeature feature ||
                !Status.Features.Any(f => f.FeatureId == featureId && f.PointId == DisplayPoints.Viewport))
                throw new InvalidOperationException($"Feature is unavailable: {featureId}");
            ViewportRequests++;
            try { return Task.FromResult(action(feature)); }
            catch (Exception ex) { return Task.FromException<TResult>(ex); }
        }
    }
}

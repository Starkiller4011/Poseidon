using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Poseidon.Core.Modules;
using Poseidon.Modules.Display;
using Poseidon.Tests.Shared;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(CoreDisplay.Tests.TestApplication))]

namespace CoreDisplay.Tests;

public sealed class TestApplication : Application
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<TestApplication>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public sealed class ViewportTests
{
    private const string ViewportFeature = "core.display.viewport";

    [Fact]
    public void Manifest_DeclaresSingleViewportPointBackedByTheSharedContract()
    {
        var manifest = ModuleDeclarationTests.DeployedManifest();
        var point = Assert.Single(manifest.Provides);
        Assert.Equal(DisplayPoints.Viewport, point.Id);
        Assert.Equal(PointCardinality.Single, point.Cardinality);
        Assert.Equal(typeof(IChartViewport).FullName, point.Contract);
        Assert.Equal("1.0.0", point.Version);
        var contribution = Assert.Single(manifest.Contributions);
        Assert.Equal(DisplayPoints.Viewport, contribution.Point);
        Assert.Equal(ViewportFeature, contribution.Id);
        Assert.False(contribution.Replaces);
        Assert.IsAssignableFrom<IChartViewport>(((IModule)new DisplayModule()).Features[ViewportFeature]);
    }

    [Fact]
    public void Viewport_StartsWithOnlyTheLockedBaseLayer()
    {
        var layer = Assert.Single(new ChartViewport().Layers);
        Assert.Equal(new ViewportLayer("display.base", "Land and coastline", ViewportLayerKind.Base, true), layer);
    }

    [Fact]
    public void BaseLayer_CannotBeHidden()
    {
        var viewport = new ChartViewport();
        Assert.Throws<InvalidOperationException>(() => viewport.SetLayerVisibility("display.base", false));
        viewport.SetLayerVisibility("display.base", true);
        Assert.True(Assert.Single(viewport.Layers).IsVisible);
    }

    [Fact]
    public void Overlays_StackAboveTheBaseAndToggle()
    {
        var viewport = new ChartViewport();
        viewport.AddLayer(new("test.depth", "Depth contours", ViewportLayerKind.Overlay, true));
        viewport.AddLayer(new("test.lights", "Lights", ViewportLayerKind.Overlay, false));
        Assert.Equal(["display.base", "test.depth", "test.lights"], viewport.Layers.Select(l => l.Id));
        viewport.SetLayerVisibility("test.depth", false);
        viewport.SetLayerVisibility("test.lights", true);
        Assert.False(viewport.Layers.Single(l => l.Id == "test.depth").IsVisible);
        Assert.True(viewport.Layers.Single(l => l.Id == "test.lights").IsVisible);
    }

    [Fact]
    public void UnknownDuplicateOrSecondBaseLayers_AreRejected()
    {
        var viewport = new ChartViewport();
        Assert.Throws<ArgumentException>(() => viewport.SetLayerVisibility("missing", true));
        Assert.Throws<ArgumentException>(() =>
            viewport.AddLayer(new("display.base", "Duplicate", ViewportLayerKind.Overlay, true)));
        Assert.Throws<ArgumentException>(() =>
            viewport.AddLayer(new("test.base", "Another base", ViewportLayerKind.Base, true)));
        Assert.Single(viewport.Layers);
    }

    [AvaloniaFact]
    public void CreateView_ReturnsANewEmptyViewWithAnExplicitNoChartDataState()
    {
        IChartViewport viewport = new ChartViewport();
        var first = Assert.IsAssignableFrom<Control>(viewport.CreateView());
        var second = Assert.IsAssignableFrom<Control>(viewport.CreateView());
        Assert.NotSame(first, second);
        var window = new Window { Content = first };
        try
        {
            window.Show();
            Assert.Contains(first.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == "No chart data loaded");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task CreateView_RejectsCallsOffTheUiThread()
    {
        IChartViewport viewport = new ChartViewport();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Task.Run(viewport.CreateView));
    }
}

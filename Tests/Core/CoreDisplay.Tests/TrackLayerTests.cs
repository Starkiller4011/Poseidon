using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Poseidon.Core.Modules;
using Poseidon.Modules.Display;
using Poseidon.Tests.Shared;
using Xunit;

namespace CoreDisplay.Tests;

public sealed class TrackLayerTests
{
    private const string ViewportFeature = "core.display.viewport";
    private static readonly DateTimeOffset Time = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private static Track Track(string id, bool positioned = true)
        => new(id, null, positioned ? TrackKind.Point : TrackKind.Bearing,
            TrackState.Active, positioned ? new GeodeticPosition(25, 55) : null, null, null, null,
            null, null, Time, Time, Time, 1, 1, []);

    private static Track TrackWithBearing(string id, TrackKind kind, TrackState state = TrackState.Active)
    {
        var bearing = new TrackBearing(45, 5, new GeodeticPosition(25, 55), Time);
        var position = kind == TrackKind.Bearing ? null : new GeodeticPosition(25, 55);
        return new(id, null, kind, state, position, null, null, null, null, bearing, Time, Time, Time, 1, 1, []);
    }

    private static IChartViewport ViewportOf(IModule module) => (IChartViewport)module.Features[ViewportFeature];

    private static string? StatusText(Control view)
        => view.GetLogicalDescendants().OfType<TextBlock>()
            .FirstOrDefault(t => t.Name == "TrackStatus" && t.IsVisible)?.Text;

    private static async Task WaitForStatus(Control view, string expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (StatusText(view) != expected)
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for '{expected}', last '{StatusText(view)}'.");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
    }

    [Fact]
    public void Viewport_OffersAToggleableTracksOverlayAboveTheLockedBase()
    {
        var viewport = ViewportOf(new DisplayModule());
        Assert.Equal(["display.base", "display.tracks"], viewport.Layers.Select(l => l.Id));
        var tracks = viewport.Layers[1];
        Assert.Equal((ViewportLayerKind.Overlay, true), (tracks.Kind, tracks.IsVisible));
        viewport.SetLayerVisibility("display.tracks", false);
        Assert.False(viewport.Layers[1].IsVisible);
        Assert.Throws<InvalidOperationException>(() => viewport.SetLayerVisibility("display.base", false));
    }

    [Fact]
    public async Task BearingLines_IncludeBearingAndHybridTracksOnly_StyledByEachTracksOwnState()
    {
        // R37: Bearing and Hybrid tracks each produce one line, styled by that track's own state; a Point
        // track never does, even one whose Bearing happens to be populated (a not-yet-refining contribution,
        // per the Track.Bearing/TrackContribution comments in Tracks.cs). Fails at the BearingLines stub
        // (NotImplementedException) until slice 5 Step 3 implements the real filtering.
        var feed = new StaticTrackFeed();
        feed.Set(
            TrackWithBearing("T1", TrackKind.Bearing, TrackState.Active),
            TrackWithBearing("T2", TrackKind.Hybrid, TrackState.Stale),
            TrackWithBearing("T3", TrackKind.Point));
        var host = new FakeTrackHost { Feed = feed };
        var module = new DisplayModule(TimeSpan.FromMilliseconds(20));
        var viewport = ViewportOf(module);
        await module.StartAsync(host, null, CancellationToken.None);
        await Task.Delay(100);

        var lines = viewport.BearingLines;
        Assert.Equal(
            new[] { ("T1", TrackState.Active), ("T2", TrackState.Stale) },
            lines.Select(l => (l.TrackId, l.State)).OrderBy(l => l.Item1, StringComparer.Ordinal));
        Assert.All(lines, l => Assert.Equal((new GeodeticPosition(25, 55), 45.0), (l.Origin, l.TrueBearingDegrees)));
        await module.StopAsync(CancellationToken.None);
    }

    [AvaloniaFact]
    public async Task View_ShowsTheLiveTrackCount_FollowsTheLayerToggle_AndReportsAMissingProvider()
    {
        var feed = new StaticTrackFeed();
        feed.Set(Track("T1"), Track("T2", positioned: false));
        var host = new FakeTrackHost { Feed = feed };
        IModule module = new DisplayModule(TimeSpan.FromMilliseconds(20));
        var viewport = ViewportOf(module);
        var view = Assert.IsAssignableFrom<Control>(viewport.CreateView());
        var window = new Window { Content = view };
        try
        {
            window.Show();
            await WaitForStatus(view, "Tracks: waiting for data");

            await module.StartAsync(host, null, CancellationToken.None);
            await WaitForStatus(view, "Tracks: 2 (1 without position)");

            feed.Set(Track("T1"), Track("T2"), Track("T3"));
            await WaitForStatus(view, "Tracks: 3");

            viewport.SetLayerVisibility("display.tracks", false);
            Dispatcher.UIThread.RunJobs();
            Assert.Null(StatusText(view));
            viewport.SetLayerVisibility("display.tracks", true);
            await WaitForStatus(view, "Tracks: 3");

            host.Feed = null;
            await WaitForStatus(view, "Tracks unavailable");

            host.Feed = feed;
            await WaitForStatus(view, "Tracks: 3");

            await module.StopAsync(CancellationToken.None);
            await module.StopAsync(CancellationToken.None);
            await WaitForStatus(view, "Tracks: waiting for data");
            Assert.Contains(view.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == "No chart data loaded");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task WithoutATracksProvider_DisplayStillRunsAndSaysTracksAreUnavailable()
    {
        var host = new FakeTrackHost();
        IModule module = new DisplayModule(TimeSpan.FromMilliseconds(20));
        var view = Assert.IsAssignableFrom<Control>(ViewportOf(module).CreateView());
        var window = new Window { Content = view };
        try
        {
            window.Show();
            await module.StartAsync(host, null, CancellationToken.None);
            await WaitForStatus(view, "Tracks unavailable");
            await module.StopAsync(CancellationToken.None);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ClosedViews_StopFollowingTheTrackFeed()
    {
        var feed = new StaticTrackFeed();
        var host = new FakeTrackHost { Feed = feed };
        IModule module = new DisplayModule(TimeSpan.FromMilliseconds(20));
        var view = Assert.IsAssignableFrom<Control>(ViewportOf(module).CreateView());
        var window = new Window { Content = view };
        window.Show();
        await module.StartAsync(host, null, CancellationToken.None);
        feed.Set(Track("T1"));
        await WaitForStatus(view, "Tracks: 1");

        window.Close();
        feed.Set(Track("T1"), Track("T2"));
        await Task.Delay(200);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Tracks: 1", view.GetLogicalDescendants().OfType<TextBlock>()
            .First(t => t.Name == "TrackStatus").Text);
        await module.StopAsync(CancellationToken.None);
    }
}

using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Poseidon.Core.Modules;

namespace Poseidon.Modules.Display;

internal sealed class ChartViewport : IChartViewport
{
    internal const string BaseLayerId = "display.base";
    internal const string TracksLayerId = "display.tracks";
    private readonly List<ViewportLayer> layers = [new(BaseLayerId, "Land and coastline", ViewportLayerKind.Base, true)];
    private readonly TrackReplica? tracks;

    internal ChartViewport(TrackReplica? tracks = null) => this.tracks = tracks;

    public IReadOnlyList<ViewportLayer> Layers => [.. layers];

    // Raised on any thread after a layer's visibility changed.
    internal event Action? LayersChanged;

    // No chart provider exists yet, so every view shows the explicit empty state. Track symbols need the chart
    // projection, so for now the tracks overlay reports what it has received.
    public object CreateView()
    {
        Dispatcher.UIThread.VerifyAccess();
        var content = new Grid();
        content.Children.Add(new TextBlock
        {
            Text = "No chart data loaded",
            Foreground = Brushes.WhiteSmoke,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        if (tracks is not null)
            content.Children.Add(CreateTrackStatus(tracks));
        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x1D, 0x2E)),
            Child = content
        };
    }

    private TextBlock CreateTrackStatus(TrackReplica replica)
    {
        var status = new TextBlock
        {
            Name = "TrackStatus",
            Foreground = Brushes.LightSteelBlue,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Avalonia.Thickness(8)
        };
        void Refresh()
        {
            var overlay = layers.FirstOrDefault(l => l.Id == TracksLayerId);
            status.IsVisible = overlay?.IsVisible ?? false;
            status.Text = DescribeTracks(replica);
        }
        void OnChanged() => Dispatcher.UIThread.Post(Refresh);
        // The view follows the feed only while it is on screen, so closed views release their subscriptions.
        status.AttachedToVisualTree += (_, _) =>
        {
            replica.Changed += OnChanged;
            LayersChanged += OnChanged;
            Refresh();
        };
        status.DetachedFromVisualTree += (_, _) =>
        {
            replica.Changed -= OnChanged;
            LayersChanged -= OnChanged;
        };
        Refresh();
        return status;
    }

    private static string DescribeTracks(TrackReplica replica)
    {
        switch (replica.State)
        {
            case TrackReplicaState.Live:
                var unpositioned = replica.Tracks.Count(t => t.Position is null);
                return unpositioned == 0
                    ? $"Tracks: {replica.Tracks.Count}"
                    : $"Tracks: {replica.Tracks.Count} ({unpositioned} without position)";
            case TrackReplicaState.Unavailable:
                return "Tracks unavailable";
            default:
                return "Tracks: waiting for data";
        }
    }

    public void SetLayerVisibility(string layerId, bool visible)
    {
        var index = layers.FindIndex(l => l.Id == layerId);
        if (index < 0)
            throw new ArgumentException($"Unknown viewport layer: {layerId}", nameof(layerId));
        if (layers[index].Kind == ViewportLayerKind.Base && !visible)
            throw new InvalidOperationException($"The base layer {layerId} cannot be hidden.");
        layers[index] = layers[index] with { IsVisible = visible };
        LayersChanged?.Invoke();
    }

    internal void AddLayer(ViewportLayer layer)
    {
        if (layer.Kind == ViewportLayerKind.Base)
            throw new ArgumentException("The viewport already has its base layer.", nameof(layer));
        if (layers.Any(l => l.Id == layer.Id))
            throw new ArgumentException($"Duplicate viewport layer: {layer.Id}", nameof(layer));
        layers.Add(layer);
    }
}

using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Poseidon.Core.Modules;

namespace Poseidon.Modules.Display;

internal sealed class ChartViewport : IChartViewport
{
    internal const string BaseLayerId = "display.base";
    private readonly List<ViewportLayer> layers = [new(BaseLayerId, "Land and coastline", ViewportLayerKind.Base, true)];

    public IReadOnlyList<ViewportLayer> Layers => [.. layers];

    // No chart provider exists yet, so every view shows the explicit empty state.
    public object CreateView()
    {
        Dispatcher.UIThread.VerifyAccess();
        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x1D, 0x2E)),
            Child = new TextBlock
            {
                Text = "No chart data loaded",
                Foreground = Brushes.WhiteSmoke,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
    }

    public void SetLayerVisibility(string layerId, bool visible)
    {
        var index = layers.FindIndex(l => l.Id == layerId);
        if (index < 0)
            throw new ArgumentException($"Unknown viewport layer: {layerId}", nameof(layerId));
        if (layers[index].Kind == ViewportLayerKind.Base && !visible)
            throw new InvalidOperationException($"The base layer {layerId} cannot be hidden.");
        layers[index] = layers[index] with { IsVisible = visible };
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

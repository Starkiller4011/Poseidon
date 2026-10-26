namespace Poseidon.Core.Modules;

// Points declared by the Display module. The IDs are shared so consumers such as CoreUI can find the
// effective contribution in RuntimeStatus without referencing Display.
public static class DisplayPoints
{
    public const string Viewport = "display.viewport";
}

public enum ViewportLayerKind
{
    // Always drawn and cannot be hidden (land and coastline).
    Base,
    // Toggleable thematic content stacked above the base.
    Overlay
}

public sealed record ViewportLayer(string Id, string DisplayName, ViewportLayerKind Kind, bool IsVisible);

// The chart viewport contributed to DisplayPoints.Viewport. The contracts assembly stays independent of
// any UI framework, so CreateView returns an Avalonia control as object. It must be called on the UI
// thread; each call returns a new view that the caller owns and must release when it detaches or stops.
public interface IChartViewport
{
    object CreateView();

    // Bottom-to-top drawing order.
    IReadOnlyList<ViewportLayer> Layers { get; }

    void SetLayerVisibility(string layerId, bool visible);
}

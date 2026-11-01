using Poseidon.Core.Modules;

namespace CoreUI.Tests;

// A fake IModuleHost that can hold any number of simultaneous typed features at once, for the
// Multiple-cardinality points this feature introduces (coreui.sidepanel, coreui.page,
// coreui.page.panel, coreui.tid.page), unlike ShellViewportTests' ViewportHost, which only ever holds
// one (display.viewport is Single-cardinality). Also records keyboard-shortcut calls (H13 to H16),
// since IModuleHost's defaults for those just throw.
internal sealed class MultiFeatureHost : IModuleHost
{
    private readonly Dictionary<string, object> features = new(StringComparer.Ordinal);
    private readonly List<FeatureStatus> featureStatuses = [];
    private readonly List<(UiKey Key, UiKeyModifiers Modifiers, string? Scope, Action Invoke)> shortcuts = [];

    public RuntimeStatus Status { get; private set; } = new(3,
        [new("example.module", "1.0.0", "package")], [], ["example diagnostic"]);

    public int ReloadRequests { get; private set; }
    public int ShutdownRequests { get; private set; }
    public string? ActiveScope { get; private set; }
    public List<(UiKey Key, UiKeyModifiers Modifiers)> DispatchCalls { get; } = [];

    public void Provide<T>(string pointId, string featureId, string providerId, T value) where T : class
    {
        features[featureId] = value;
        featureStatuses.Add(new FeatureStatus(featureId, providerId, pointId));
        Status = Status with { Features = [.. featureStatuses] };
    }

    public void Withdraw(string featureId)
    {
        features.Remove(featureId);
        featureStatuses.RemoveAll(f => f.FeatureId == featureId);
        Status = Status with { Features = [.. featureStatuses], Generation = Status.Generation + 1 };
    }

    public void RequestReload() => ReloadRequests++;
    public void RequestShutdown() => ShutdownRequests++;

    public Task<TResult> InvokeAsync<TFeature, TResult>(string featureId, Func<TFeature, TResult> action,
        CancellationToken cancellationToken = default) where TFeature : class
    {
        if (features.TryGetValue(featureId, out var value) && value is TFeature feature)
        {
            try { return Task.FromResult(action(feature)); }
            catch (Exception ex) { return Task.FromException<TResult>(ex); }
        }
        throw new InvalidOperationException($"Feature is unavailable: {featureId}");
    }

    public IDisposable RegisterShortcut(UiKey key, UiKeyModifiers modifiers, string? scope, Action invoke)
    {
        if (shortcuts.Any(s => s.Key == key && s.Modifiers == modifiers &&
                                (s.Scope == scope || s.Scope is null || scope is null)))
            throw new InvalidOperationException($"A shortcut for {modifiers}+{key} already exists in an overlapping scope.");
        var entry = (key, modifiers, scope, invoke);
        shortcuts.Add(entry);
        return new Unregister(() => shortcuts.Remove(entry));
    }

    public void SetActiveShortcutScope(string? scope) => ActiveScope = scope;

    public bool DispatchShortcut(UiKey key, UiKeyModifiers modifiers)
    {
        DispatchCalls.Add((key, modifiers));
        var match = shortcuts.FirstOrDefault(s => s.Key == key && s.Modifiers == modifiers &&
                                                   (s.Scope == ActiveScope || s.Scope is null));
        if (match.Invoke is null)
            return false;
        match.Invoke();
        return true;
    }

    private sealed class Unregister(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}

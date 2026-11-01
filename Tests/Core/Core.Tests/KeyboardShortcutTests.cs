using Poseidon.Core.Modules;
using Poseidon.Core.Runtime;
using Xunit;

namespace Core.Tests;

// H13 to H15: IModuleHost.RegisterShortcut/SetActiveShortcutScope/DispatchShortcut have no real
// implementation yet (the interface's defaults just throw NotImplementedException, so that every
// existing IModuleHost test double keeps compiling unchanged); ModuleRuntime is the one real
// implementation this feature adds them to. Every test below fails, uncaught, against that default.
public sealed class KeyboardShortcutTests : IAsyncDisposable
{
    private readonly ModuleRuntime runtimeImpl = new(
        Path.Combine(Path.GetTempPath(), "Poseidon.Tests", Guid.NewGuid().ToString("N")));

    // IModuleHost.RegisterShortcut/SetActiveShortcutScope/DispatchShortcut are default interface
    // members; calling them through the concrete ModuleRuntime type would not see ModuleRuntime's own
    // override (C# only dispatches to it through the interface), so tests go through this instead.
    private IModuleHost runtime => runtimeImpl;

    [Fact]
    public void RegisterShortcut_ReturnsADisposableHandle()
    {
        using var registration = runtime.RegisterShortcut(UiKey.F1, UiKeyModifiers.None, null, () => { });
        Assert.NotNull(registration);
    }

    [Fact]
    public void ConflictingRegistration_OnOverlappingScope_ThrowsImmediately()
    {
        using var first = runtime.RegisterShortcut(UiKey.F1, UiKeyModifiers.Control, "navigation", () => { });
        Assert.Throws<InvalidOperationException>(
            () => runtime.RegisterShortcut(UiKey.F1, UiKeyModifiers.Control, "navigation", () => { }));
        // A global (null-scope) registration overlaps every scope, and vice versa.
        Assert.Throws<InvalidOperationException>(
            () => runtime.RegisterShortcut(UiKey.F1, UiKeyModifiers.Control, null, () => { }));
    }

    [Fact]
    public void NonOverlappingScopes_DoNotConflict()
    {
        using var navigation = runtime.RegisterShortcut(UiKey.F1, UiKeyModifiers.None, "navigation", () => { });
        using var sensors = runtime.RegisterShortcut(UiKey.F1, UiKeyModifiers.None, "sensors", () => { });
        Assert.NotNull(navigation);
        Assert.NotNull(sensors);
    }

    [Fact]
    public void Dispatch_InvokesTheRegistrationForTheActiveScope_AndReturnsTrue()
    {
        var invoked = 0;
        using var registration = runtime.RegisterShortcut(UiKey.F1, UiKeyModifiers.None, "navigation", () => invoked++);
        runtime.SetActiveShortcutScope("navigation");
        Assert.True(runtime.DispatchShortcut(UiKey.F1, UiKeyModifiers.None));
        Assert.Equal(1, invoked);
    }

    [Fact]
    public void Dispatch_DoesNothing_ForAGestureScopedToADifferentInactiveScope()
    {
        var invoked = 0;
        using var registration = runtime.RegisterShortcut(UiKey.F1, UiKeyModifiers.None, "navigation", () => invoked++);
        runtime.SetActiveShortcutScope("sensors");
        Assert.False(runtime.DispatchShortcut(UiKey.F1, UiKeyModifiers.None));
        Assert.Equal(0, invoked);
    }

    [Fact]
    public void Dispatch_InvokesAGlobalRegistration_RegardlessOfTheActiveScope()
    {
        var invoked = 0;
        using var registration = runtime.RegisterShortcut(UiKey.Escape, UiKeyModifiers.None, null, () => invoked++);
        runtime.SetActiveShortcutScope("anything");
        Assert.True(runtime.DispatchShortcut(UiKey.Escape, UiKeyModifiers.None));
        Assert.Equal(1, invoked);
    }

    [Fact]
    public void DisposingTheHandle_RemovesTheRegistration_SoAReloadLeavesNoStaleOrDuplicate()
    {
        var registration = runtime.RegisterShortcut(UiKey.F1, UiKeyModifiers.None, "navigation", () => { });
        registration.Dispose();
        runtime.SetActiveShortcutScope("navigation");
        Assert.False(runtime.DispatchShortcut(UiKey.F1, UiKeyModifiers.None));
        // Re-registering the same gesture after disposal must not be treated as a conflict.
        using var again = runtime.RegisterShortcut(UiKey.F1, UiKeyModifiers.None, "navigation", () => { });
        Assert.NotNull(again);
    }

    public async ValueTask DisposeAsync() => await runtimeImpl.DisposeAsync();
}

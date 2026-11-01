using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Poseidon.Core.Modules;
using Poseidon.Modules.CoreUI;
using Xunit;
using static CoreUI.Tests.ShellViewportTests;

namespace CoreUI.Tests;

// H12: diagnostics, reload and exit controls are removed from CoreUI's shell entirely (development-time
// conveniences that belong with a future configuration-editing tool instead). The current
// implementation still shows them (M1-B behavior), so these tests fail until that is removed.
public sealed class ShellChromeTests
{
    [AvaloniaFact]
    public async Task Shell_HasNoDiagnosticsStatusReloadOrExitUi()
    {
        var host = new MultiFeatureHost();
        var module = new CoreUiModule();
        try
        {
            await module.StartAsync(host, null, CancellationToken.None);
            var window = Assert.IsType<Window>(module.ActiveWindow);
            await Task.Delay(650);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(),
                b => Equals(b.Content, "Rescan / reload modules"));
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, "Exit"));
            Assert.DoesNotContain("example diagnostic", Text(window));
            Assert.DoesNotContain("example.module", Text(window));
        }
        finally { await module.StopAsync(CancellationToken.None); }
    }

    [AvaloniaFact]
    public async Task ClosingAWindow_StillRequestsAnOrderlyShutdown()
    {
        // The dedicated status/reload/exit panel is gone, but closing a window must still shut the
        // application down in an orderly way (host.RequestShutdown()), same as today.
        var host = new MultiFeatureHost();
        var module = new CoreUiModule();
        try
        {
            await module.StartAsync(host, null, CancellationToken.None);
            var window = Assert.IsType<Window>(module.ActiveWindow);
            window.Close();
            Assert.Equal(1, host.ShutdownRequests);
        }
        finally { await module.StopAsync(CancellationToken.None); }
    }
}

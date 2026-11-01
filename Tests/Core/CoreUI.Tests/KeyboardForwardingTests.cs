using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Poseidon.Core.Modules;
using Poseidon.Modules.CoreUI;
using Xunit;

namespace CoreUI.Tests;

// H16: CoreUI does not forward raw key events to DispatchShortcut, or keep SetActiveShortcutScope in
// sync with the active control page, yet. Every test below fails because MultiFeatureHost's spy never
// records a call.
public sealed class KeyboardForwardingTests
{
    [AvaloniaFact]
    public async Task KeyPressOnTheMainWindow_IsForwardedToDispatchShortcut()
    {
        var host = new MultiFeatureHost();
        var module = new CoreUiModule();
        try
        {
            await module.StartAsync(host, null, CancellationToken.None);
            var window = Assert.IsType<Window>(module.ActiveWindow);
            window.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent, Key = Key.F1, KeyModifiers = KeyModifiers.Control
            });
            Assert.Contains((UiKey.F1, UiKeyModifiers.Control), host.DispatchCalls);
        }
        finally { await module.StopAsync(CancellationToken.None); }
    }

    [AvaloniaFact]
    public async Task SwitchingTheActiveControlPage_UpdatesTheHostsActiveShortcutScope()
    {
        var host = new MultiFeatureHost();
        host.Provide(CoreUiPoints.Page, "nav.page", "Core.Navigation", new FakePage("navigation", "Navigation"));
        host.Provide(CoreUiPoints.Page, "sensors.page", "Core.Radar", new FakePage("sensors", "Sensors"));
        var module = new CoreUiModule();
        try
        {
            await module.StartAsync(host, null, CancellationToken.None);
            var window = Assert.IsType<Window>(module.ActiveWindow);
            await ShellViewportTests.WaitUntil(() => ShellViewportTests.Text(window).Contains("Sensors"));

            Assert.Single(window.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, "Sensors"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await ShellViewportTests.WaitUntil(() => host.ActiveScope == "sensors");
        }
        finally { await module.StopAsync(CancellationToken.None); }
    }

    private sealed class FakePage(string pageId, string displayName) : IUiPage
    {
        public string PageId => pageId;
        public string DisplayName => displayName;
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Poseidon.Core.Modules;
using Poseidon.Modules.CoreUI;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(CoreUI.Tests.TestApplication))]

namespace CoreUI.Tests;

public sealed class TestApplication : Application
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<TestApplication>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public sealed class UiLifecycleTests
{
    [AvaloniaFact]
    public async Task Start_ShowsStatusAndBindsReloadExitCommands()
    {
        var host = new TestHost();
        var module = new CoreUiModule();
        try
        {
            await module.StartAsync(host, null, CancellationToken.None);
            var window = Assert.IsType<Window>(module.ActiveWindow);
            Assert.Equal("Poseidon - generation 3", window.Title);
            var text = string.Join("\n", window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
            Assert.Contains("example.module", text);
            Assert.Contains("example.feature -> example.module", text);
            Assert.Contains("example diagnostic", text);
            Click(window, "Rescan / reload modules");
            Click(window, "Exit");
            Assert.Equal(1, host.ReloadRequests);
            Assert.Equal(1, host.ShutdownRequests);
        }
        finally { await module.StopAsync(CancellationToken.None); }
    }

    [AvaloniaFact]
    public async Task UserClose_RequestsShutdownButStopClosesWithoutRequestingAgain()
    {
        var host = new TestHost();
        var module = new CoreUiModule();
        try
        {
            await module.StartAsync(host, null, CancellationToken.None);
            var window = Assert.IsType<Window>(module.ActiveWindow);
            window.Close();
            Assert.Equal(1, host.ShutdownRequests);
            Assert.True(window.IsVisible);
            await module.StopAsync(CancellationToken.None);
            Assert.False(window.IsVisible);
            Assert.Equal(1, host.ShutdownRequests);
            await module.StopAsync(CancellationToken.None);
        }
        finally { await module.StopAsync(CancellationToken.None); }
    }

    [AvaloniaFact]
    public async Task Restart_ReplacesWindowAndRefreshesGeneration()
    {
        var host = new TestHost();
        var module = new CoreUiModule();
        try
        {
            await module.StopAsync(CancellationToken.None);
            await module.StartAsync(host, null, CancellationToken.None);
            var original = Assert.IsType<Window>(module.ActiveWindow);
            host.Status = host.Status with { Generation = 4 };
            await Task.Delay(650);
            Assert.Equal("Poseidon - generation 4", original.Title);
            await module.StopAsync(CancellationToken.None);
            await module.StartAsync(host, null, CancellationToken.None);
            Assert.NotSame(original, module.ActiveWindow);
            Assert.False(original.IsVisible);
        }
        finally { await module.StopAsync(CancellationToken.None); }
    }

    [AvaloniaFact]
    public async Task UnsupportedState_DoesNotCreateAWindow()
    {
        var module = new CoreUiModule();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            module.StartAsync(new TestHost(), new ModuleState(2, []), CancellationToken.None).AsTask());
        Assert.Null(module.ActiveWindow);
    }

    [AvaloniaFact]
    public async Task RecoveryWindow_ShowsPersistentFailureAndRequestsShutdown()
    {
        var host = new TestHost();
        var window = new Poseidon.MainWindow(host);
        try
        {
            window.ShowFailure("initialization failed");
            await Task.Delay(650);
            Assert.True(window.IsVisible);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),
                t => t.Text == "initialization failed");
            Click(window, "Exit");
            Assert.Equal(1, host.ShutdownRequests);
            Click(window, "Rescan modules");
            Assert.Equal(1, host.ReloadRequests);
            window.Close();
            Assert.Equal(2, host.ShutdownRequests);
            Assert.True(window.IsVisible);
        }
        finally
        {
            window.Stop();
            window.Hide();
        }
    }

    [AvaloniaFact]
    public async Task RecoveryWindow_HidesWhenCoreUiLoadsAndReturnsWhenItDisappears()
    {
        var host = new TestHost();
        var window = new Poseidon.MainWindow(host);
        try
        {
            window.Show();
            await Task.Delay(650);
            Assert.True(window.IsVisible);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),
                t => t.Text == "example diagnostic");
            // Only a shell contribution hides the recovery window, whatever the module is called.
            host.Status = host.Status with { Modules = [new("Core.UI", "1.0.0", "package")] };
            await Task.Delay(650);
            Assert.True(window.IsVisible);
            host.Status = host.Status with
            {
                Features = [new("any.shell", "Vendor.Shell", Poseidon.Core.Modules.ExtensionPoints.Shell)]
            };
            await Task.Delay(650);
            Assert.False(window.IsVisible);
            host.Status = host.Status with { Modules = [], Features = [] };
            await Task.Delay(650);
            Assert.True(window.IsVisible);
        }
        finally
        {
            window.Stop();
            window.Hide();
        }
    }

    private static void Click(Window window, string label)
        => Assert.Single(window.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, label))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private sealed class TestHost : IModuleHost
    {
        public RuntimeStatus Status { get; set; } = new(3,
            [new("example.module", "1.0.0", "package")],
            [new("example.feature", "example.module")], ["example diagnostic"]);
        public int ReloadRequests { get; private set; }
        public int ShutdownRequests { get; private set; }
        public void RequestReload() => ReloadRequests++;
        public void RequestShutdown() => ShutdownRequests++;
        public Task<TResult> InvokeAsync<TFeature, TResult>(string featureId, Func<TFeature, TResult> action,
            CancellationToken cancellationToken = default) where TFeature : class
            => throw new NotSupportedException("UI status must not invoke a feature.");
    }
}

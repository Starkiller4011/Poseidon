using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Poseidon.Core.Modules;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("CoreUI.Tests")]

namespace Poseidon.Modules.CoreUI;

public sealed class CoreUiModule : IModule, IShell
{
    private const string Unavailable = "Chart viewport unavailable";
    private Window? window;
    private DispatcherTimer? timer;
    private ContentControl? viewportHost;
    // The viewport provider (feature, generation) that is attached or was last attempted.
    private (string FeatureId, long Generation)? viewportSource;
    private bool attachingViewport;
    private bool stopping;
    internal Window? ActiveWindow => window;
    public IReadOnlyList<ModuleCapability> Capabilities => [];
    public IReadOnlyDictionary<string, object> Features => new Dictionary<string, object> { ["core.ui.shell"] = this };

    public async ValueTask StartAsync(IModuleHost host, ModuleState? state, CancellationToken cancellationToken)
    {
        if (state is not null)
            throw new InvalidDataException("CoreUI does not accept persisted state yet.");
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            stopping = false;
            var status = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
            var reload = new Button { Content = "Rescan / reload modules" };
            reload.Click += (_, _) => host.RequestReload();
            var exit = new Button { Content = "Exit" };
            exit.Click += (_, _) => host.RequestShutdown();
            var panel = new StackPanel { Margin = new Thickness(20), Spacing = 12 };
            panel.Children.Add(new TextBlock { Text = "Poseidon", FontSize = 28 });
            panel.Children.Add(new TextBlock
            {
                Text = "Reloadable application foundation. Chart and sensor functionality is not implemented yet."
            });
            panel.Children.Add(reload);
            panel.Children.Add(exit);
            panel.Children.Add(status);
            viewportHost = new ContentControl { Margin = new Thickness(12) };
            var sidePanel = new ScrollViewer { Content = panel };
            Grid.SetColumn(sidePanel, 1);
            var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("*,380") };
            layout.Children.Add(viewportHost);
            layout.Children.Add(sidePanel);
            window = new Window
            {
                Title = "Poseidon", Width = 1000, Height = 650,
                Content = layout,
                WindowStartupLocation = WindowStartupLocation.CenterScreen
            };
            window.Closing += (_, e) =>
            {
                if (!stopping)
                {
                    e.Cancel = true;
                    host.RequestShutdown();
                }
            };
            void Refresh()
            {
                var snapshot = host.Status;
                if (window is not null)
                    window.Title = $"Poseidon - generation {snapshot.Generation}";
                status.Text = $"Generation: {snapshot.Generation}; retired contexts awaiting collection: " +
                    $"{snapshot.RetainedLoadContexts}\n\nPackages:\n" +
                    string.Join("\n", snapshot.Packages.Select(FormatPackage)) +
                    "\n\nModules:\n" +
                    string.Join("\n", snapshot.Modules.Select(m => $"{m.ModuleId}  {m.Version}")) +
                    "\n\nEffective features:\n" +
                    string.Join("\n", snapshot.Features.Select(f => $"{f.FeatureId} -> {f.ProviderId}")) +
                    "\n\nDiagnostics:\n" + string.Join("\n", snapshot.Diagnostics);
            }
            // Feature calls are rejected inside lifecycle methods, so the viewport is attached from the
            // timer rather than during StartAsync.
            timer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background,
                (_, _) =>
                {
                    Refresh();
                    _ = UpdateViewportAsync(host, host.Status);
                });
            Refresh();
            window.Show();
        });
    }

    private async Task UpdateViewportAsync(IModuleHost host, RuntimeStatus snapshot)
    {
        if (attachingViewport || viewportHost is null)
            return;
        var provider = snapshot.Features.FirstOrDefault(f => f.PointId == DisplayPoints.Viewport);
        if (provider is null)
        {
            if (viewportSource is not null || viewportHost.Content is null)
            {
                viewportSource = null;
                viewportHost.Content = Placeholder($"{Unavailable}: no module provides {DisplayPoints.Viewport}.");
            }
            return;
        }
        var source = (provider.FeatureId, snapshot.Generation);
        // A failed provider is not retried until the provider or generation changes.
        if (viewportSource == source)
            return;
        viewportSource = source;
        viewportHost.Content = null;
        attachingViewport = true;
        try
        {
            var view = await host.InvokeAsync<IChartViewport, object>(provider.FeatureId, v => v.CreateView());
            if (stopping || viewportHost is null || viewportSource != source)
                return;
            viewportHost.Content = view as Control ??
                Placeholder($"{Unavailable}: {provider.ProviderId} returned an unsupported view.");
        }
        catch (Exception ex)
        {
            if (!stopping && viewportHost is not null && viewportSource == source)
                viewportHost.Content = Placeholder($"{Unavailable}: {ex.Message}");
        }
        finally { attachingViewport = false; }
    }

    private static TextBlock Placeholder(string text) => new()
    {
        Text = text,
        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };

    internal static string FormatPackage(PackageStatus package)
    {
        var name = package.ModuleId is null
            ? Path.GetFileName(package.PackagePath)
            : $"{package.ModuleId} {package.Version}";
        return package.Detail is null ? $"{package.State}: {name}" : $"{package.State}: {name} - {package.Detail}";
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            stopping = true;
            timer?.Stop();
            timer = null;
            // Release the hosted view so no window keeps a retired provider's load context alive.
            if (viewportHost is not null)
                viewportHost.Content = null;
            viewportHost = null;
            viewportSource = null;
            window?.Close();
            window = null;
        });
    }
}

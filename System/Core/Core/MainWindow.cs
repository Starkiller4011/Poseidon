using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Poseidon.Core.Modules;

namespace Poseidon;

// This recovery window must remain usable when no module contributes a shell.
public sealed class MainWindow : Window
{
    private readonly TextBlock status = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly DispatcherTimer timer;
    private string? startupFailure;

    public MainWindow(IModuleHost runtime)
    {
        Title = "Poseidon - module recovery";
        Width = 650;
        Height = 400;
        var reload = new Button { Content = "Rescan modules" };
        reload.Click += (_, _) => runtime.RequestReload();
        var exit = new Button { Content = "Exit" };
        exit.Click += (_, _) => runtime.RequestShutdown();
        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = "Waiting for the CoreUI module", FontSize = 20 });
        panel.Children.Add(reload);
        panel.Children.Add(exit);
        panel.Children.Add(status);
        Content = new ScrollViewer { Content = panel };
        Closing += (_, e) =>
        {
            e.Cancel = true;
            runtime.RequestShutdown();
        };
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) =>
        {
            var snapshot = runtime.Status;
            var packages = string.Join("\n", snapshot.Packages
                .Where(p => p.State is not PackageState.Active)
                .Select(p => $"{p.State}: {p.ModuleId ?? System.IO.Path.GetFileName(p.PackagePath)} - {p.Detail}"));
            status.Text = startupFailure ?? string.Join("\n\n",
                new[] { packages, string.Join("\n", snapshot.Diagnostics) }.Where(s => s.Length > 0));
            if (snapshot.HasContribution(ExtensionPoints.Shell))
                Hide();
            else if (!IsVisible)
                Show();
        });
    }

    public void ShowFailure(string message)
    {
        startupFailure = message;
        status.Text = message;
        Show();
    }

    public void Stop() => timer.Stop();
}

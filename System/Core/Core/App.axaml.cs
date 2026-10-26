using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Poseidon.Core.Runtime;

namespace Poseidon;

public partial class App : Application
{
    private ModuleRuntime? runtime;
    private MainWindow? recovery;
    private SplashWindow? splash;
    private bool exiting;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            runtime = new ModuleRuntime(shutdown: () => Dispatcher.UIThread.Post(() => Shutdown(desktop)));
            splash = new SplashWindow();
            desktop.ShutdownRequested += (_, e) =>
            {
                if (!exiting)
                {
                    e.Cancel = true;
                    Shutdown(desktop);
                }
            };
            _ = StartRuntimeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task StartRuntimeAsync()
    {
        string? failure = null;
        try { await splash!.ShowWhileLoadingAsync(() => Task.Run(() => runtime!.StartAsync())); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Cannot start module runtime: {ex}");
            failure = ex.Message;
        }
        if (exiting)
            return;
        recovery = new MainWindow(runtime!);
        if (failure is not null)
            recovery.ShowFailure(failure);
        else if (!runtime!.Status.HasContribution(Poseidon.Core.Modules.ExtensionPoints.Shell))
            recovery.Show();
    }

    private async void Shutdown(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (exiting)
            return;
        exiting = true;
        try
        {
            if (runtime is not null)
                await runtime.DisposeAsync();
        }
        catch (Exception ex) { Console.Error.WriteLine($"Module shutdown failed: {ex}"); }
        finally
        {
            recovery?.Stop();
            splash?.Close();
            desktop.Shutdown();
        }
    }
}

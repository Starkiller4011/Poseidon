using Poseidon.Core.Modules;
using Poseidon.Modules.Charts.Inbox;
using Poseidon.Modules.Charts.Store;

namespace Poseidon.Modules.Charts;

public sealed class ChartsModule : IModule
{
    private readonly ChartLibrary library;
    private CancellationTokenSource? stopping;

    public ChartsModule()
        : this(Path.Combine(AppContext.BaseDirectory, "charts"))
    {
    }

    // chartsDirectory holds the store (charts.db), the inbox and the inbox record (CI9).
    public ChartsModule(string chartsDirectory)
    {
        ChartsDirectory = chartsDirectory;
        library = new ChartLibrary(new ChartLibraryOptions { DatabasePath = Path.Combine(chartsDirectory, "charts.db") });
    }

    public string ChartsDirectory { get; }

    // The inbox scan started by StartAsync (CI5); null before the module has started.
    public Task? StartupScan { get; private set; }

    public IReadOnlyList<ModuleCapability> Capabilities =>
    [
        new ModuleCapability("charts.display", "Chart Display"),
        new ModuleCapability("charts.route-planning", "Route Planning")
    ];

    public IReadOnlyDictionary<string, object> Features => new Dictionary<string, object>
    {
        [ChartPoints.LibraryFeatureId] = library
    };

    // The inbox is checked once per start, in the background, so starting never waits on an import (CI5).
    public ValueTask StartAsync(IModuleHost host, ModuleState? state, CancellationToken cancellationToken)
    {
        if (state is not null)
            throw new InvalidDataException("This module does not support state migration.");
        // The shared module contract starts modules without a host, and test hosts may not implement logging.
        ILog? log;
        try
        {
            log = host?.GetLog("Core.Charts");
        }
        catch (NotImplementedException)
        {
            log = null;
        }
        library.UseLog(log);
        var inbox = new ChartInbox(library, new ChartInboxOptions
        {
            InboxPath = Path.Combine(ChartsDirectory, "inbox"),
            RecordPath = Path.Combine(ChartsDirectory, "inbox.json"),
            Log = log
        });
        var cancel = new CancellationTokenSource();
        stopping = cancel;
        StartupScan = Task.Run(async () =>
        {
            try
            {
                await inbox.ScanAsync(cancel.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
            }
            catch (Exception e)
            {
                // A scan failure is reported, never a reason for the module to stop working (CI8).
                log?.Error("The chart inbox scan failed.", e);
            }
        });
        return ValueTask.CompletedTask;
    }

    // Cancels a scan still running and waits for it; the published chart is never left half-imported (CI6).
    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        var cancel = Interlocked.Exchange(ref stopping, null);
        if (cancel is null)
            return;
        await cancel.CancelAsync().ConfigureAwait(false);
        if (StartupScan is { } scan)
            await scan.WaitAsync(cancellationToken).ConfigureAwait(false);
        cancel.Dispose();
    }
}

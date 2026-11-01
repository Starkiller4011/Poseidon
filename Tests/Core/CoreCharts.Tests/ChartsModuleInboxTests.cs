using Poseidon.Core.Modules;
using Poseidon.Modules.Charts;
using Poseidon.Modules.Charts.Inbox;
using Xunit;

namespace CoreCharts.Tests;

// The module's startup inbox scan (CI1, CI5-CI9), driven through IModule as the runtime drives it.
public sealed class ChartsModuleInboxTests
{
    private const string X01SW = "10100AA_X01SW";
    private static readonly TimeSpan ScanTimeout = TimeSpan.FromMinutes(2);

    // CI5
    [Fact]
    public async Task Start_ReturnsBeforeTheInboxIsImportedThenImportsItInTheBackground()
    {
        using var charts = new TempCopy();
        Inboxes.AddSet(Path.Combine(charts.Root, "inbox"), "powerup/S100_ROOT", Fixtures.ReleasedPowerUp);
        var module = new ChartsModule(charts.Root);

        await ((IModule)module).StartAsync(new ChartsHost(), null, CancellationToken.None);
        var scan = Assert.IsAssignableFrom<Task>(module.StartupScan);
        Assert.False(scan.IsCompleted);

        await scan.WaitAsync(ScanTimeout);
        Assert.Equal(6, Inboxes.LibraryOf(module).Current.Cells.Count);
        await ((IModule)module).StopAsync(CancellationToken.None);
    }

    // CI5: the inbox is not monitored while running; a set added then is imported at the next start.
    [Fact]
    public async Task SetAddedWhileRunning_IsImportedAtTheNextStart()
    {
        using var charts = new TempCopy();
        var inbox = Path.Combine(charts.Root, "inbox");
        Inboxes.AddSet(inbox, "powerup/S100_ROOT", Fixtures.ReleasedPowerUp);
        var module = await Inboxes.Start(charts.Root, new ChartsHost());
        await module.StartupScan!.WaitAsync(ScanTimeout);

        Inboxes.AddSet(inbox, "update/S100_ROOT", Fixtures.SequentialUpdate(1));
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.Equal(0, Libraries.Cell(Inboxes.LibraryOf(module).Current, X01SW).Update);
        await ((IModule)module).StopAsync(CancellationToken.None);

        var restarted = await Inboxes.Start(charts.Root, new ChartsHost());
        await restarted.StartupScan!.WaitAsync(ScanTimeout);
        Assert.Equal(1, Libraries.Cell(Inboxes.LibraryOf(restarted).Current, X01SW).Update);
        await ((IModule)restarted).StopAsync(CancellationToken.None);
    }

    // CI1
    [Fact]
    public async Task Start_WithoutAnInbox_CreatesIt()
    {
        using var charts = new TempCopy();

        var module = await Inboxes.Start(charts.Root, new ChartsHost());
        await module.StartupScan!.WaitAsync(ScanTimeout);

        Assert.True(Directory.Exists(Path.Combine(charts.Root, "inbox")));
        await ((IModule)module).StopAsync(CancellationToken.None);
    }

    // CI6: whether the stop lands before or after the import publishes, the record and the chart agree,
    // and the next start finishes the job.
    [Fact]
    public async Task Stop_DuringTheStartupScan_WaitsForItAndLeavesTheRecordConsistent()
    {
        using var charts = new TempCopy();
        Inboxes.AddSet(Path.Combine(charts.Root, "inbox"), "powerup/S100_ROOT", Fixtures.ReleasedPowerUp);
        var module = await Inboxes.Start(charts.Root, new ChartsHost());

        await ((IModule)module).StopAsync(CancellationToken.None);

        Assert.True(module.StartupScan!.IsCompleted);
        var library = Inboxes.LibraryOf(module);
        var recorded = Inboxes.Create(library, charts.Root).Records.Any(r => r.State == InboxSetState.Completed);
        Assert.Equal(recorded ? 6 : 0, library.Current.Cells.Count);
        await ((IModule)module).StopAsync(CancellationToken.None);

        var restarted = await Inboxes.Start(charts.Root, new ChartsHost());
        await restarted.StartupScan!.WaitAsync(ScanTimeout);
        Assert.Equal(6, Inboxes.LibraryOf(restarted).Current.Cells.Count);
        Assert.Equal(InboxSetState.Completed,
            Inboxes.Record(Inboxes.Create(Inboxes.LibraryOf(restarted), charts.Root), "powerup/S100_ROOT").State);
        await ((IModule)restarted).StopAsync(CancellationToken.None);
    }

    // CI7
    [Fact]
    public async Task Start_LogsEachSetsOutcomeToTheChartsLog()
    {
        using var charts = new TempCopy();
        Inboxes.AddSet(Path.Combine(charts.Root, "inbox"), "powerup/S100_ROOT", Fixtures.ReleasedPowerUp);
        var host = new ChartsHost();

        var module = await Inboxes.Start(charts.Root, host);
        await module.StartupScan!.WaitAsync(ScanTimeout);

        Assert.Contains("Core.Charts", host.LogCategories);
        Assert.Contains(host.Log.Entries, e => e.Message.Contains("powerup/S100_ROOT", StringComparison.Ordinal)
            && e.Message.Contains(nameof(ChartImportStatus.Loaded), StringComparison.Ordinal));
        await ((IModule)module).StopAsync(CancellationToken.None);
    }

    // CI8
    [Fact]
    public async Task Start_WithAnUnusableInbox_StillStartsAndLogsTheProblem()
    {
        using var charts = new TempCopy();
        File.WriteAllText(Path.Combine(charts.Root, "inbox"), "a file where the inbox folder should be");
        var host = new ChartsHost();

        var module = await Inboxes.Start(charts.Root, host);
        await module.StartupScan!.WaitAsync(ScanTimeout);

        Assert.True(module.StartupScan.IsCompletedSuccessfully);
        Assert.Contains(host.Log.Entries, e => e.Level == LogLevel.Error);
        await ((IModule)module).StopAsync(CancellationToken.None);
    }

    // CI9
    [Fact]
    public void DefaultModule_KeepsItsChartsBesideTheHost()
        => Assert.Equal(Path.Combine(AppContext.BaseDirectory, "charts"), new ChartsModule().ChartsDirectory);

    // CI9
    [Fact]
    public async Task Start_KeepsTheStoreInboxAndRecordInTheChartsDirectory()
    {
        using var charts = new TempCopy();
        Inboxes.AddSet(Path.Combine(charts.Root, "inbox"), "powerup/S100_ROOT", Fixtures.ReleasedPowerUp);

        var module = await Inboxes.Start(charts.Root, new ChartsHost());
        await module.StartupScan!.WaitAsync(ScanTimeout);

        Assert.True(File.Exists(Path.Combine(charts.Root, "charts.db")));
        Assert.True(File.Exists(Path.Combine(charts.Root, "inbox.json")));
        Assert.True(Directory.Exists(Path.Combine(charts.Root, "inbox")));
        await ((IModule)module).StopAsync(CancellationToken.None);
    }
}

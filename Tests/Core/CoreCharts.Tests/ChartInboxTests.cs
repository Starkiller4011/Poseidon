using Poseidon.Core.Modules;
using Poseidon.Modules.Charts;
using Poseidon.Modules.Charts.Inbox;
using Poseidon.Modules.Charts.Store;
using Xunit;

namespace CoreCharts.Tests;

public sealed class ChartInboxTests
{
    private const string X01SW = "10100AA_X01SW";

    // CI1
    [Fact]
    public async Task Scan_FindsEverySetUpToEightLevelsDeepWhateverTheCatalogueCase()
    {
        using var charts = new TempCopy();
        var inbox = Path.Combine(charts.Root, "inbox");
        Inboxes.AddSet(inbox, "top/S100_ROOT", Fixtures.ReleasedPowerUp);
        var lower = Inboxes.AddSet(inbox, "media/2026/lower/S100_ROOT", Fixtures.ReleasedPowerUp);
        File.Move(Path.Combine(lower, "CATALOG.XML"), Path.Combine(lower, "renaming.tmp"));
        File.Move(Path.Combine(lower, "renaming.tmp"), Path.Combine(lower, "catalog.xml"));
        Inboxes.AddSet(inbox, "1/2/3/4/5/6/7/S100_ROOT", Fixtures.ReleasedPowerUp);
        Inboxes.AddSet(inbox, "1/2/3/4/5/6/7/8/S100_ROOT", Fixtures.ReleasedPowerUp);
        File.WriteAllText(Path.Combine(inbox, "readme.txt"), "not an exchange set");
        Directory.CreateDirectory(Path.Combine(inbox, "empty"));
        await using var library = Inboxes.Library(charts.Root);

        var result = await Inboxes.Create(library, charts.Root).ScanAsync();

        Assert.Equal(["1/2/3/4/5/6/7/S100_ROOT", "media/2026/lower/S100_ROOT", "top/S100_ROOT"],
            result.Processed.Select(r => r.RelativePath).Order(StringComparer.Ordinal));
    }

    // CI1
    [Fact]
    public async Task Scan_CatalogueInsideAFoundSet_IsNotAnotherSet()
    {
        using var charts = new TempCopy();
        var inbox = Path.Combine(charts.Root, "inbox");
        Inboxes.AddSet(inbox, "outer/S100_ROOT", Fixtures.ReleasedPowerUp);
        Inboxes.AddSet(inbox, "outer/S100_ROOT/extra/S100_ROOT", Fixtures.SequentialUpdate(1));
        await using var library = Inboxes.Library(charts.Root);

        var result = await Inboxes.Create(library, charts.Root).ScanAsync();

        Assert.Equal("outer/S100_ROOT", Assert.Single(result.Processed).RelativePath);
        Assert.Equal(0, Libraries.Cell(library.Current, X01SW).Update);
    }

    // CI2
    [Fact]
    public async Task Scan_UnchangedCompletedSet_IsSkippedIncludingAfterARestart()
    {
        using var charts = new TempCopy();
        Inboxes.AddSet(Path.Combine(charts.Root, "inbox"), "powerup/S100_ROOT", Fixtures.ReleasedPowerUp);
        await using var library = Inboxes.Library(charts.Root);
        var first = await Inboxes.Create(library, charts.Root).ScanAsync();
        Assert.Equal(InboxSetState.Completed, Assert.Single(first.Processed).State);
        var generation = library.Current.Generation;

        // A new ChartInbox reads only the durable record, as the next start would.
        var second = await Inboxes.Create(library, charts.Root).ScanAsync();

        Assert.Empty(second.Processed);
        Assert.Equal(["powerup/S100_ROOT"], second.Skipped);
        Assert.Equal(generation, library.Current.Generation);
    }

    // CI2
    [Theory]
    [InlineData("touched")]
    [InlineData("added")]
    [InlineData("removed")]
    public async Task Scan_SetWithAFileChanged_IsImportedAgain(string change)
    {
        using var charts = new TempCopy();
        var root = Inboxes.AddSet(Path.Combine(charts.Root, "inbox"), "powerup/S100_ROOT", Fixtures.ReleasedPowerUp);
        await using var library = Inboxes.Library(charts.Root);
        var inbox = Inboxes.Create(library, charts.Root);
        var fingerprint = Assert.Single((await inbox.ScanAsync()).Processed).Fingerprint;
        var signature = Path.Combine(root, "CATALOG.SIGN");
        switch (change)
        {
            case "touched":
                File.SetLastWriteTimeUtc(signature, File.GetLastWriteTimeUtc(signature).AddMinutes(5));
                break;
            case "added":
                File.WriteAllText(Path.Combine(root, "README.TXT"), "added after the first import");
                break;
            case "removed":
                File.Delete(signature);
                break;
        }

        var again = await inbox.ScanAsync();

        var record = Assert.Single(again.Processed);
        Assert.Equal("powerup/S100_ROOT", record.RelativePath);
        Assert.Equal(InboxSetState.Completed, record.State);
        Assert.NotEqual(fingerprint, record.Fingerprint);
        Assert.Empty(again.Skipped);
    }

    // CI3: delivery order is not name order; the update is retried once its base has installed.
    [Fact]
    public async Task Scan_UpdateBeforeItsBase_IsRetriedAfterTheBaseInstalls()
    {
        using var charts = new TempCopy();
        var inboxPath = Path.Combine(charts.Root, "inbox");
        Inboxes.AddSet(inboxPath, "a-update/S100_ROOT", Fixtures.SequentialUpdate(1));
        Inboxes.AddSet(inboxPath, "b-base/S100_ROOT", Fixtures.ReleasedPowerUp);
        await using var library = Inboxes.Library(charts.Root);
        var inbox = Inboxes.Create(library, charts.Root);

        var result = await inbox.ScanAsync();

        Assert.Equal(1, Libraries.Cell(library.Current, X01SW).Update);
        Assert.Equal(["a-update/S100_ROOT", "b-base/S100_ROOT"], result.Processed.Select(r => r.RelativePath));
        Assert.All(inbox.Records, r => Assert.Equal(InboxSetState.Completed, r.State));
        Assert.Equal(ChartImportStatus.Loaded, Inboxes.Record(inbox, "a-update/S100_ROOT").Status);
    }

    // CI3
    [Fact]
    public async Task Scan_UpdateWhoseBaseNeverArrives_StaysPendingAndIsRetriedAtTheNextStart()
    {
        using var charts = new TempCopy();
        var inboxPath = Path.Combine(charts.Root, "inbox");
        Inboxes.AddSet(inboxPath, "a-update/S100_ROOT", Fixtures.SequentialUpdate(1));
        await using var library = Inboxes.Library(charts.Root);

        var first = Inboxes.Create(library, charts.Root);
        await first.ScanAsync();
        var pending = Inboxes.Record(first, "a-update/S100_ROOT");
        Assert.Equal(InboxSetState.Pending, pending.State);
        Assert.Contains(pending.ErrorDiagnostics, d => d.Code == ChartDiagnosticCodes.BaseNotInstalled);

        var second = await Inboxes.Create(library, charts.Root).ScanAsync();
        Assert.Equal(InboxSetState.Pending, Assert.Single(second.Processed, r => r.RelativePath == "a-update/S100_ROOT").State);

        Inboxes.AddSet(inboxPath, "b-base/S100_ROOT", Fixtures.ReleasedPowerUp);
        var third = Inboxes.Create(library, charts.Root);
        await third.ScanAsync();

        Assert.Equal(1, Libraries.Cell(library.Current, X01SW).Update);
        Assert.All(third.Records, r => Assert.Equal(InboxSetState.Completed, r.State));
    }

    // CI3: the SHOM sets named so the edition 2 update comes first and the edition 1 set last.
    [LocalDataFact]
    public async Task Scan_ShomSetsOutOfOrder_EndAtEdition2Update1()
    {
        using var charts = new TempCopy();
        var inboxPath = Path.Combine(charts.Root, "inbox");
        var sets = Directory.EnumerateDirectories(Staging.ShomSource, "S100_ROOT", SearchOption.AllDirectories).ToList();
        string Source(string marker) => sets.Single(s => s.Replace('\\', '/').Contains(marker, StringComparison.Ordinal));
        Inboxes.AddSet(inboxPath, "1/S100_ROOT", Source("_ER1_ed2_"));
        Inboxes.AddSet(inboxPath, "2/S100_ROOT", Source("_Ed2_"));
        Inboxes.AddSet(inboxPath, "3/S100_ROOT", Source("_ed1_ER1_2_"));
        Inboxes.AddSet(inboxPath, "4/S100_ROOT", Source("S101FR00571300_"));
        await using var library = Inboxes.Library(charts.Root);
        var inbox = Inboxes.Create(library, charts.Root);

        await inbox.ScanAsync();

        foreach (var name in new[] { "101FR00369400", "101FR00571300" })
        {
            var cell = Libraries.Cell(library.Current, name);
            Assert.Equal((2, 1, ChartLineageState.Current), (cell.Edition, cell.Update, cell.State));
        }
        Assert.All(inbox.Records, r => Assert.Equal(InboxSetState.Completed, r.State));
    }

    // CI6
    [Fact]
    public async Task Scan_Cancelled_ThrowsAndDoesNotRecordTheSetBeingImported()
    {
        using var charts = new TempCopy();
        Inboxes.AddSet(Path.Combine(charts.Root, "inbox"), "powerup/S100_ROOT", Fixtures.ReleasedPowerUp);
        var library = new BlockingLibrary();
        using var cancel = new CancellationTokenSource();

        var scan = Inboxes.Create(library, charts.Root).ScanAsync(cancel.Token);
        await library.Entered.Task.WaitAsync(TimeSpan.FromMinutes(1));
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan);
        Assert.Empty(Inboxes.Create(library, charts.Root).Records);
    }

    // CI7
    [Fact]
    public async Task Scan_RecordsAndLogsEachSetsOutcome()
    {
        using var charts = new TempCopy();
        var root = Inboxes.AddSet(Path.Combine(charts.Root, "inbox"), "damaged/S100_ROOT", Fixtures.ReleasedPowerUp);
        var file = Fixtures.DatasetFile(root, "10100AA_X02SE.000");
        File.WriteAllBytes(file, File.ReadAllBytes(file)[..^15]);
        using var reference = new TempCopy();
        await using var direct = Libraries.Create(reference.Root);
        var expected = await direct.ImportAsync(root);
        var processedAt = new DateTimeOffset(2026, 11, 1, 18, 0, 0, TimeSpan.Zero);
        var log = new RecordingLog();
        await using var library = Inboxes.Library(charts.Root);
        var options = new ChartInboxOptions
        {
            InboxPath = Path.Combine(charts.Root, "inbox"),
            RecordPath = Path.Combine(charts.Root, "inbox.json"),
            Time = new FixedTime(processedAt),
            Log = log
        };

        var result = await new ChartInbox(library, options).ScanAsync();

        var record = Assert.Single(result.Processed);
        Assert.Equal(("damaged/S100_ROOT", InboxSetState.Completed, ChartImportStatus.Loaded, 5, processedAt),
            (record.RelativePath, record.State, record.Status, record.CellsChanged, record.ProcessedAt));
        Assert.Equal(
            (expected.Diagnostics.Count(d => d.Severity == ChartDiagnosticSeverity.Error),
                expected.Diagnostics.Count(d => d.Severity == ChartDiagnosticSeverity.Warning),
                expected.Diagnostics.Count(d => d.Severity == ChartDiagnosticSeverity.Information)),
            (record.Errors, record.Warnings, record.Information));
        var error = Assert.Single(record.ErrorDiagnostics);
        Assert.EndsWith("10100AA_X02SE.000", error.File, StringComparison.Ordinal);
        Assert.Equal(Canonical.Of(record), Canonical.Of(Inboxes.Record(new ChartInbox(library, options), "damaged/S100_ROOT")));
        Assert.Contains(log.Entries, e => e.Message.Contains("damaged/S100_ROOT", StringComparison.Ordinal)
            && e.Message.Contains(nameof(ChartImportStatus.Loaded), StringComparison.Ordinal));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("10100AA_X02SE.000", StringComparison.Ordinal));
    }

    // CI8
    [Fact]
    public async Task Scan_CorruptRecord_IsLoggedSetAsideAndItsSetsImportedAgain()
    {
        using var charts = new TempCopy();
        Inboxes.AddSet(Path.Combine(charts.Root, "inbox"), "powerup/S100_ROOT", Fixtures.ReleasedPowerUp);
        await using var library = Inboxes.Library(charts.Root);
        await Inboxes.Create(library, charts.Root).ScanAsync();
        File.WriteAllText(Path.Combine(charts.Root, "inbox.json"), "{ this is not a record");
        var log = new RecordingLog();

        var result = await Inboxes.Create(library, charts.Root, log).ScanAsync();

        Assert.Equal("powerup/S100_ROOT", Assert.Single(result.Processed).RelativePath);
        Assert.Contains(log.Entries, e => e.Level >= LogLevel.Warning && e.Message.Contains("inbox.json", StringComparison.Ordinal));
        Assert.Single(Directory.EnumerateFiles(charts.Root, "inbox.json.unreadable-*"));
    }

    // CI8
    [Fact]
    public async Task Scan_InboxThatCannotBeRead_IsLoggedAndImportsNothing()
    {
        using var charts = new TempCopy();
        File.WriteAllText(Path.Combine(charts.Root, "inbox"), "a file where the inbox folder should be");
        await using var library = Inboxes.Library(charts.Root);
        var log = new RecordingLog();

        var result = await Inboxes.Create(library, charts.Root, log).ScanAsync();

        Assert.Empty(result.Processed);
        Assert.Empty(result.Skipped);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error);
    }

    // CI8
    [Fact]
    public async Task Scan_ImportThatThrows_IsLoggedAndTheOtherSetsStillImport()
    {
        using var charts = new TempCopy();
        var inboxPath = Path.Combine(charts.Root, "inbox");
        Inboxes.AddSet(inboxPath, "a/S100_ROOT", Fixtures.SequentialUpdate(1));
        Inboxes.AddSet(inboxPath, "b/S100_ROOT", Fixtures.ReleasedPowerUp);
        await using var real = Inboxes.Library(charts.Root);
        var library = new ThrowingLibrary(real, "a");
        var log = new RecordingLog();
        var inbox = Inboxes.Create(library, charts.Root, log);

        await inbox.ScanAsync();

        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("a/S100_ROOT", StringComparison.Ordinal));
        Assert.Equal(6, real.Current.Cells.Count);
        Assert.DoesNotContain(inbox.Records, r => r.RelativePath == "a/S100_ROOT" && r.State == InboxSetState.Completed);
    }

    // Holds every import until it is cancelled, then reports it cancelled as ChartLibrary does.
    private sealed class BlockingLibrary : IChartLibrary
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ChartImportResult> ImportAsync(string exchangeSetRoot, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
            return new ChartImportResult(ChartImportStatus.Cancelled, 0, [], []);
        }

        public ChartNeighbourhood QueryNeighbourhood(IReadOnlyList<string> referenceCells) => new(0, []);
    }

    // Throws for the set whose top folder is named failing; every other import goes to the real library.
    private sealed class ThrowingLibrary(IChartLibrary inner, string failing) : IChartLibrary
    {
        public Task<ChartImportResult> ImportAsync(string exchangeSetRoot, CancellationToken cancellationToken = default)
            => exchangeSetRoot.Replace('\\', '/').Contains($"/{failing}/", StringComparison.Ordinal)
                ? throw new InvalidOperationException("Simulated import defect.")
                : inner.ImportAsync(exchangeSetRoot, cancellationToken);

        public ChartNeighbourhood QueryNeighbourhood(IReadOnlyList<string> referenceCells) => inner.QueryNeighbourhood(referenceCells);
    }
}

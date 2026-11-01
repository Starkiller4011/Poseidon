using System.Diagnostics;
using Poseidon.Core.Modules;
using Xunit;
using Xunit.Abstractions;

namespace CoreCharts.Tests;

// C30: a first numeric baseline, recorded in the test output (and copied into the plan's ledger); not a
// pass/fail performance gate.
public sealed class ChartBaselineTests(ITestOutputHelper output)
{
    [Fact]
    public Task Baseline_ReleasedPowerUp_IsMeasured() => Measure("released S-164 PowerUp (6 cells)", Fixtures.ReleasedPowerUp);

    [LocalDataFact]
    public Task Baseline_UkhoExchangeSet_IsMeasured() => Measure("UKHO S-101_GB_Apr26 (27 cells)", Fixtures.LocalUkhoGb);

    private async Task Measure(string label, string root)
    {
        using var store = new TempCopy();
        await using var library = Libraries.Create(store.Root);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);

        var load = Stopwatch.StartNew();
        var result = await library.ImportAsync(root);
        load.Stop();

        Assert.Equal(ChartImportStatus.Loaded, result.Status);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        var peakWorkingSet = Process.GetCurrentProcess().PeakWorkingSet64;
        var names = library.Current.Cells.Select(c => c.DatasetName).ToArray();
        const int queries = 200;
        var query = Stopwatch.StartNew();
        for (var i = 0; i < queries; i++)
            Assert.NotEmpty(library.QueryNeighbourhood([names[i % names.Length]]).Cells);
        query.Stop();

        output.WriteLine($"C30 baseline, {label}: load {load.Elapsed.TotalMilliseconds:F0} ms, " +
            $"allocated {allocated / 1048576.0:F1} MiB, peak working set {peakWorkingSet / 1048576.0:F1} MiB, " +
            $"query {query.Elapsed.TotalMilliseconds / queries:F3} ms mean over {queries}");
    }
}

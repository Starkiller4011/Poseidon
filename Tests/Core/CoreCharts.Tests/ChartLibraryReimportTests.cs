using Poseidon.Core.Modules;
using Poseidon.Modules.Charts;
using Xunit;

namespace CoreCharts.Tests;

// CI4: importing bytes the store already holds is a no-op, so retried and re-staged inbox sets are harmless.
public sealed class ChartLibraryReimportTests
{
    private const string X01SW = "10100AA_X01SW";

    [Fact]
    public async Task Import_SameExchangeSetAgain_IsAlreadyInstalledAndPublishesNothing()
    {
        using var store = new TempCopy();
        await using var library = Libraries.Create(store.Root);
        await Libraries.ImportLoaded(library, Fixtures.ReleasedPowerUp);
        var generation = library.Current.Generation;
        var cells = Canonical.Of(library.Current.Cells);

        var again = await library.ImportAsync(Fixtures.ReleasedPowerUp);

        Assert.Equal(ChartImportStatus.Loaded, again.Status);
        Assert.Empty(again.Cells);
        Assert.Equal(generation, again.Generation);
        Assert.Equal(generation, library.Current.Generation);
        Assert.Equal(cells, Canonical.Of(library.Current.Cells));
        Assert.DoesNotContain(again.Diagnostics, d => d.Severity == ChartDiagnosticSeverity.Error);
        Assert.Equal(6, again.Diagnostics.Count(d => d.Code == ChartDiagnosticCodes.AlreadyInstalled
            && d.Severity == ChartDiagnosticSeverity.Information));
    }

    [Fact]
    public async Task Import_BaseOrUpdateAgainAfterTheUpdate_IsAlreadyInstalledNotOutOfSequence()
    {
        using var store = new TempCopy();
        await using var library = Libraries.Create(store.Root);
        await Libraries.ImportLoaded(library, Fixtures.ReleasedPowerUp);
        await Libraries.ImportLoaded(library, Fixtures.SequentialUpdate(1));
        var generation = library.Current.Generation;

        var baseAgain = await library.ImportAsync(Fixtures.ReleasedPowerUp);
        var updateAgain = await library.ImportAsync(Fixtures.SequentialUpdate(1));

        foreach (var (result, file) in new[] { (baseAgain, "10100AA_X01SW.000"), (updateAgain, "10100AA_X01SW.001") })
        {
            Assert.Equal(ChartImportStatus.Loaded, result.Status);
            Assert.Empty(result.Cells);
            Assert.DoesNotContain(result.Diagnostics, d => d.Severity == ChartDiagnosticSeverity.Error);
            Assert.Contains(result.Diagnostics, d => d.Code == ChartDiagnosticCodes.AlreadyInstalled
                && (d.File ?? "").EndsWith(file, StringComparison.Ordinal));
        }
        Assert.Equal(1, Libraries.Cell(library.Current, X01SW).Update);
        Assert.Equal(generation, library.Current.Generation);
    }
}

using Poseidon.Modules.Charts.ExchangeSets;
using Poseidon.Modules.Charts.Lineage;
using Poseidon.Modules.Charts.S101;
using Xunit;

namespace CoreCharts.Tests;

public sealed class ReaderBoundaryTests
{
    // C28: each seam works on its own inputs, with none of the others involved.
    [Fact]
    public void Seams_AreUsableIndependently()
    {
        IExchangeSetReader reader = new ExchangeSetReader();
        IS101DatasetDecoder decoder = new S101DatasetDecoder();
        IS101UpdateApplier applier = new S101UpdateApplier();

        // Catalogue parsing without decoding any dataset.
        var set = reader.Read(Fixtures.ReleasedPowerUp).ExchangeSet;
        Assert.NotNull(set);
        Assert.NotEmpty(set.Entries);

        // Decoding a bare dataset file that belongs to no exchange set.
        var bare = Path.Combine(Fixtures.S101TestDatasets, "S-101_1.2", "000", "101AA00DS0001.000");
        var decoded = decoder.Decode(File.ReadAllBytes(bare), Path.GetFileName(bare));
        Assert.Equal(DecodeStatus.Decoded, decoded.Status);

        // Applying an update to decoded datasets without any catalogue or store.
        var baseCell = Decoding.Dataset(Fixtures.DatasetFile(Fixtures.ReleasedPowerUp, "10100AA_X01SW.000"));
        var update = Decoding.Dataset(Fixtures.DatasetFile(Fixtures.SequentialUpdate(1), "10100AA_X01SW.001"));
        var result = applier.Apply(DatasetLineage.Start(baseCell), update);
        Assert.Equal(UpdateOutcome.Applied, result.Outcome);
    }
}

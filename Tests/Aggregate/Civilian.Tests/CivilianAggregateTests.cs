using AIS.Tests;
using Charts.Tests;
using Core.Tests;
using Tracks.Tests;
using Xunit;

namespace Civilian.Tests;

public sealed class CivilianAggregateTests
{
    [Fact]
    public void CivilianSuite_Runs_ModuleSmokeTests()
    {
        CoreTestSuite.RunSmoke();
        ChartsTestSuite.RunSmoke();
        AisTestSuite.RunSmoke();
        TracksTestSuite.RunSmoke();
    }
}

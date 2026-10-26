using Core.Tests;
using CoreAIS.Tests;
using CoreCharts.Tests;
using CoreTracks.Tests;
using Xunit;

namespace Civilian.Tests;

public sealed class CivilianAggregateTests
{
    [Fact]
    public void CivilianSuite_Runs_ModuleSmokeTests()
    {
        CoreTestSuite.RunSmoke();
        CoreChartsTestSuite.RunSmoke();
        CoreAISTestSuite.RunSmoke();
        CoreTracksTestSuite.RunSmoke();
    }
}

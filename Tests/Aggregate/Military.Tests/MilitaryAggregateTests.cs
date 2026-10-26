using CmsCore.Tests;
using CmsComms.Tests;
using CmsEW.Tests;
using CmsGun.Tests;
using CmsMissile.Tests;
using CmsRadar.Tests;
using CmsSonar.Tests;
using CmsTracks.Tests;
using CmsUI.Tests;
using Core.Tests;
using CoreAIS.Tests;
using CoreCharts.Tests;
using CoreNavigation.Tests;
using CoreRadar.Tests;
using CoreTracks.Tests;
using Xunit;

namespace Military.Tests;

public sealed class MilitaryAggregateTests
{
    [Fact]
    public void MilitarySuite_Runs_AllModuleSmokeTests()
    {
        CoreTestSuite.RunSmoke();
        CoreChartsTestSuite.RunSmoke();
        CoreAISTestSuite.RunSmoke();
        CoreTracksTestSuite.RunSmoke();
        CmsCoreTestSuite.RunSmoke();
        CoreRadarTestSuite.RunSmoke();
        CmsRadarTestSuite.RunSmoke();
        CmsSonarTestSuite.RunSmoke();
        CmsGunTestSuite.RunSmoke();
        CmsMissileTestSuite.RunSmoke();
        CmsCommsTestSuite.RunSmoke();
        CmsTracksTestSuite.RunSmoke();
        CmsUITestSuite.RunSmoke();
        CoreNavigationTestSuite.RunSmoke();
        CmsEWTestSuite.RunSmoke();
    }
}

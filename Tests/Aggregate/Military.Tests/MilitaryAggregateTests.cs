using AIS.Tests;
using Charts.Tests;
using CmsCore.Tests;
using Comms.Tests;
using Core.Tests;
using EW.Tests;
using Gun.Tests;
using Missile.Tests;
using Navigation.Tests;
using Radar.Tests;
using Sonar.Tests;
using Tracks.Tests;
using Xunit;

namespace Military.Tests;

public sealed class MilitaryAggregateTests
{
    [Fact]
    public void MilitarySuite_Runs_AllModuleSmokeTests()
    {
        CoreTestSuite.RunSmoke();
        ChartsTestSuite.RunSmoke();
        AisTestSuite.RunSmoke();
        TracksTestSuite.RunSmoke();
        CmsCoreTestSuite.RunSmoke();
        RadarTestSuite.RunSmoke();
        SonarTestSuite.RunSmoke();
        GunTestSuite.RunSmoke();
        MissileTestSuite.RunSmoke();
        CommsTestSuite.RunSmoke();
        NavigationTestSuite.RunSmoke();
        EwTestSuite.RunSmoke();
    }
}

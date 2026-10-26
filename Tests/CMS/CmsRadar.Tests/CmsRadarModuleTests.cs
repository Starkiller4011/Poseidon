using System.Reflection;
using Xunit;

namespace CmsRadar.Tests;

public static class CmsRadarTestSuite
{
    public static void RunSmoke()
    {
        var assembly = Assembly.Load("CmsRadar");
        Assert.NotNull(assembly.EntryPoint);
    }
}

public sealed class CmsRadarModuleTests
{
    [Fact]
    public void CmsRadarEntryPoint_IsAvailable()
    {
        CmsRadarTestSuite.RunSmoke();
    }
}

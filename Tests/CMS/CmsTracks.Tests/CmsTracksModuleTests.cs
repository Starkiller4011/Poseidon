using System.Reflection;
using Xunit;

namespace CmsTracks.Tests;

public static class CmsTracksTestSuite
{
    public static void RunSmoke()
    {
        var assembly = Assembly.Load("CmsTracks");
        Assert.NotNull(assembly.EntryPoint);
    }
}

public sealed class CmsTracksModuleTests
{
    [Fact]
    public void CmsTracksEntryPoint_IsAvailable()
    {
        CmsTracksTestSuite.RunSmoke();
    }
}

using System.Reflection;
using Xunit;

namespace CmsUI.Tests;

public static class CmsUITestSuite
{
    public static void RunSmoke()
    {
        var assembly = Assembly.Load("CmsUI");
        Assert.NotNull(assembly.EntryPoint);
    }
}

public sealed class CmsUIModuleTests
{
    [Fact]
    public void CmsUIEntryPoint_IsAvailable()
    {
        CmsUITestSuite.RunSmoke();
    }
}

using TokenOptimizer.Core.Projects;

namespace TokenOptimizer.Core.Tests.Projects;

public class PathSlugTests
{
    [Fact]
    public void For_IsCaseInsensitive()
    {
        Assert.Equal(PathSlug.For(@"C:\Projects\MyApp"), PathSlug.For(@"c:\projects\myapp"));
    }

    [Fact]
    public void For_IgnoresTrailingSeparator()
    {
        Assert.Equal(PathSlug.For(@"C:\Projects\MyApp"), PathSlug.For(@"C:\Projects\MyApp\"));
    }

    [Fact]
    public void For_DifferentPaths_ProduceDifferentSlugs()
    {
        Assert.NotEqual(PathSlug.For(@"C:\Projects\AppOne"), PathSlug.For(@"C:\Projects\AppTwo"));
    }

    [Fact]
    public void For_ContainsReadableLeafName()
    {
        Assert.StartsWith("myapp-", PathSlug.For(@"C:\Projects\MyApp"));
    }
}

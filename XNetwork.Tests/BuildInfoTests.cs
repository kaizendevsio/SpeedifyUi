using XNetwork.Models;

namespace XNetwork.Tests;

public class BuildInfoTests
{
    [Fact]
    public void DisplayVersion_UsesVersionInsteadOfOldDeployNumber()
    {
        var buildInfo = new BuildInfo
        {
            Version = "2026.06.8",
            DeployNumber = 23
        };

        Assert.Equal("2026.06.8", buildInfo.DisplayVersion);
    }

    [Theory]
    [InlineData("ulink-2026.06.113", "2026.06.113")]
    [InlineData("xbond-2026.06.18", "2026.06.18")]
    [InlineData("2026.06.8", "2026.06.8")]
    [InlineData("ulink-2026.06.113+abcdef", "2026.06.113")]
    [InlineData("ulink-2026.06.113-preview", "2026.06.113")]
    public void DisplayVersion_StripsReleasePrefixAndMetadata(string version, string expected)
    {
        var buildInfo = new BuildInfo { Version = version };

        Assert.Equal(expected, buildInfo.DisplayVersion);
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("1.0.0+local")]
    [InlineData("dev")]
    [InlineData("")]
    public void DisplayVersion_GenericAssemblyVersionFallsBackToCurrentRelease(string version)
    {
        var buildInfo = new BuildInfo { Version = version };

        Assert.Equal("2026.06.121", buildInfo.DisplayVersion);
        Assert.NotEqual("1.0.0", buildInfo.DisplayVersion);
    }
}

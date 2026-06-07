using XNetwork.Models;

namespace XNetwork.Tests;

public class BuildInfoTests
{
    [Fact]
    public void DisplayVersion_UsesVersionInsteadOfOldDeployNumber()
    {
        var buildInfo = new BuildInfo
        {
            Version = "2026.06.3",
            DeployNumber = 23
        };

        Assert.Equal("2026.06.3", buildInfo.DisplayVersion);
    }
}

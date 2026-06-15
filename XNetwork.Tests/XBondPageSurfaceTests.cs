namespace XNetwork.Tests;

public class XBondPageSurfaceTests
{
    [Fact]
    public void XBondPage_ExposesOnlySpeedTestAsUserFacingAction()
    {
        var content = File.ReadAllText(FindRepoFile("XNetwork", "Components", "Pages", "XBond.razor"));

        Assert.Contains("Run speed test", content);

        foreach (var text in new[]
        {
            "Run heartbeat",
            "Run multi-path",
            "Simulate bad backup",
            "Run matrix",
            "MTU sweep",
            "Run route test",
            ">Start</button>",
            ">Stop</button>",
            ">Enable boot</button>"
        })
        {
            Assert.DoesNotContain(text, content, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string FindRepoFile(params string[] pathParts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidateRoot = directory.FullName;
            if (File.Exists(Path.Combine(candidateRoot, "SpeedifyUi.sln")))
            {
                return Path.Combine([candidateRoot, .. pathParts]);
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate SpeedifyUi repository root.");
    }
}

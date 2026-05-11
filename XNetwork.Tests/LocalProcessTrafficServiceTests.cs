using XNetwork.Services;

namespace XNetwork.Tests;

public class LocalProcessTrafficServiceTests
{
    [Fact]
    public void ParseNethogsOutputReadsLatestProcessThroughput()
    {
        const string output = """
            Refreshing:
            /usr/bin/dotnet/1000/1234 12.50 250.00
            /usr/sbin/tailscaled/0/222 1.25 10.00
            total 13.75 260.00
            Refreshing:
            /usr/bin/dotnet/1000/1234 10.00 125.00
            """;

        var processes = LocalProcessTrafficService.ParseNethogsOutput(output);

        Assert.Equal(2, processes.Count);
        var dotnet = processes.Single(process => process.ProcessName == "dotnet");
        Assert.Equal(1000, dotnet.ProcessId);
        Assert.Equal(0.08, dotnet.UploadMbps, 3);
        Assert.Equal(1.0, dotnet.DownloadMbps, 3);
    }

    [Fact]
    public void ParseNethogsOutputKeepsUnknownTunnelFlows()
    {
        const string output = """
            Refreshing:
            unknown TCP/0/0 2.00 20.00
            """;

        var process = Assert.Single(LocalProcessTrafficService.ParseNethogsOutput(output));

        Assert.Equal("Unknown tunnel flow", process.ProcessName);
        Assert.Null(process.ProcessId);
        Assert.Equal(0.016, process.UploadMbps, 3);
        Assert.Equal(0.16, process.DownloadMbps, 3);
    }
}

using XNetwork.Services;

namespace XNetwork.Tests;

public class XBondSpeedTestServiceTests
{
    [Fact]
    public void ParseSpeedTestJson_ConvertsBitsPerSecondToMbps()
    {
        var result = XBondSpeedTestService.ParseSpeedTestJson(
            """
            {
              "download": 123456789.0,
              "upload": 45678901.0,
              "ping": 42.5,
              "server": {
                "sponsor": "ExampleNet",
                "name": "Singapore",
                "country": "Singapore"
              },
              "client": {
                "ip": "203.0.113.10",
                "isp": "Example ISP"
              }
            }
            """);

        Assert.Equal(123.456789, result.DownloadMbps);
        Assert.Equal(45.678901, result.UploadMbps);
        Assert.Equal(42.5, result.PingMs);
        Assert.Equal("ExampleNet Singapore", result.ServerName);
        Assert.Equal("Singapore, Singapore", result.ServerLocation);
        Assert.Equal("Example ISP", result.ClientIsp);
        Assert.Equal("203.0.113.10", result.ClientIp);
    }

    [Fact]
    public void ParseSpeedTestJson_AllowsNoisyWrapperOutput()
    {
        var result = XBondSpeedTestService.ParseSpeedTestJson(
            """
            Retrieving speedtest.net configuration...
            {"download": 1000000, "upload": 2000000, "ping": 10}
            done
            """);

        Assert.Equal(1, result.DownloadMbps);
        Assert.Equal(2, result.UploadMbps);
        Assert.Equal(10, result.PingMs);
    }

    [Fact]
    public void ParseIperfBitsPerSecond_ReadsRequestedSummary()
    {
        var bitsPerSecond = XBondSpeedTestService.ParseIperfBitsPerSecond(
            """
            {
              "end": {
                "sum_sent": {
                  "bits_per_second": 18239899.997
                },
                "sum_received": {
                  "bits_per_second": 16441950.069
                }
              }
            }
            """,
            "sum_received");

        Assert.Equal(16441950.069, bitsPerSecond);
    }

    [Fact]
    public void ParseIperfRetransmits_ReadsTcpRetransmitCount()
    {
        var retransmits = XBondSpeedTestService.ParseIperfRetransmits(
            """
            {
              "end": {
                "sum_sent": {
                  "bits_per_second": 18239899.997,
                  "retransmits": 12
                }
              }
            }
            """,
            "sum_sent");

        Assert.Equal((ulong)12, retransmits);
    }
}

using XNetwork.Services;

namespace XNetwork.Tests;

public class XBondSpeedTestServiceTests
{
    [Fact]
    public void ParseSpeedTestJson_ConvertsOfficialOoklaBytesPerSecondToMbps()
    {
        var result = XBondSpeedTestService.ParseSpeedTestJson(
            """
            {
              "type": "result",
              "ping": {
                "latency": 42.5,
                "jitter": 1.1
              },
              "download": {
                "bandwidth": 15432098
              },
              "upload": {
                "bandwidth": 5709862
              },
              "packetLoss": 0,
              "isp": "Example ISP",
              "interface": {
                "externalIp": "203.0.113.10"
              },
              "server": {
                "name": "ExampleNet",
                "host": "speedtest.example.net",
                "location": "Singapore",
                "country": "Singapore"
              }
            }
            """);

        Assert.Equal(123.456784, result.DownloadMbps);
        Assert.Equal(45.678896, result.UploadMbps);
        Assert.Equal(42.5, result.PingMs);
        Assert.Equal("ExampleNet speedtest.example.net", result.ServerName);
        Assert.Equal("Singapore, Singapore", result.ServerLocation);
        Assert.Equal("Example ISP", result.ClientIsp);
        Assert.Equal("203.0.113.10", result.ClientIp);
    }

    [Fact]
    public void ParseSpeedTestJson_AllowsNoisyOfficialOoklaWrapperOutput()
    {
        var result = XBondSpeedTestService.ParseSpeedTestJson(
            """
            Speedtest by Ookla
            {"download":{"bandwidth":125000},"upload":{"bandwidth":250000},"ping":{"latency":10}}
            done
            """);

        Assert.Equal(1, result.DownloadMbps);
        Assert.Equal(2, result.UploadMbps);
        Assert.Equal(10, result.PingMs);
    }

    [Fact]
    public void ParseSpeedTestJson_ReportsOfficialOoklaErrorLogs()
    {
        var exception = Assert.Throws<System.Text.Json.JsonException>(() =>
            XBondSpeedTestService.ParseSpeedTestJson(
                """
                {"type":"log","message":"Error: [0] Timeout occurred in connect.","level":"error"}
                """));

        Assert.Contains("Timeout occurred", exception.Message);
    }

    [Fact]
    public void ParseSpeedTestJson_PrefersOfficialOoklaResultAfterLogObject()
    {
        var result = XBondSpeedTestService.ParseSpeedTestJson(
            """
            {"type":"log","message":"Error: [0] Timeout occurred in connect.","level":"error"}{"type":"result","ping":{"latency":39.973},"download":{"bandwidth":16727582},"upload":{"bandwidth":19896672},"isp":"Vultr","interface":{"externalIp":"45.77.241.247"},"server":{"name":"SG Speedtest","location":"Singapore","country":"Singapore"}}
            """);

        Assert.Equal(133.820656, result.DownloadMbps);
        Assert.Equal(159.173376, result.UploadMbps);
        Assert.Equal(39.973, result.PingMs);
        Assert.Equal("SG Speedtest", result.ServerName);
        Assert.Equal("Singapore, Singapore", result.ServerLocation);
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

    [Fact]
    public void FormatNativeIperfFailure_ExplainsTunnelOnlyEndpoint()
    {
        var message = XBondSpeedTestService.FormatNativeIperfFailure(
            "Upload",
            "wlan0",
            "45.77.241.247",
            5202,
            "10.250.0.1",
            5201,
            "iperf3 timed out after 12 seconds.");

        Assert.Contains("native adapter wlan0", message);
        Assert.Contains("45.77.241.247:5202", message);
        Assert.Contains("tunnel-only at 10.250.0.1:5201", message);
    }

    [Fact]
    public void ResolveArtifactDirectoryCandidates_IncludesConfiguredAndFallbackPaths()
    {
        var candidates = XBondSpeedTestService.ResolveArtifactDirectoryCandidates("/var/lib/xnetwork/diagnostics");

        Assert.Equal("/var/lib/xnetwork/diagnostics", candidates[0]);
        Assert.Contains(candidates, candidate => candidate.Contains("xnetwork", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SummarizeIperfFailureDetails_ExtractsJsonError()
    {
        var details = XBondSpeedTestService.SummarizeIperfFailureDetails(
            """
            {
              "start": {
                "connected": [],
                "version": "iperf 3.17.1"
              },
              "intervals": [],
              "end": {},
              "error": "unable to connect to server"
            }
            """);

        Assert.Equal("unable to connect to server", details);
    }
}

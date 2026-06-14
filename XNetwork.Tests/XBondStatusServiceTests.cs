using Microsoft.Extensions.Logging.Abstractions;
using XNetwork.Models;
using XNetwork.Services;

namespace XNetwork.Tests;

public class XBondStatusServiceTests
{
    [Fact]
    public async Task GetStatusAsync_WhenDisabled_ReturnsSafePrototypeStatus()
    {
        var service = new XBondStatusService(
            NullLogger<XBondStatusService>.Instance,
            new XBondSettings { Enabled = false });

        var status = await service.GetStatusAsync();

        Assert.False(status.Enabled);
        Assert.False(status.Running);
        Assert.Equal("anchor-fec", status.Mode);
        Assert.Contains("disabled", status.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseStatusJson_MapsSchedulerCountersAndPathHealth()
    {
        var status = XBondStatusService.ParseStatusJson(
            """
            {
              "enabled": true,
              "running": true,
              "mode": "anchor-fec",
              "server_addr": "45.77.241.247:8444",
              "tunnel": {
                "state": "running",
                "device_name": "xbond0",
                "mtu": 1400,
                "message": "XBond tunnel is open"
              },
              "anchor_path_id": 1,
              "schedule": {
                "mode": "anchor-fec",
                "anchor_path_id": 1,
                "data_path_ids": [1],
                "duplicate_path_ids": [],
                "fec_path_ids": [2]
              },
              "paths": [
                {
                  "path_id": 1,
                  "name": "fiber",
                  "interface_name": "eth0",
                  "bind_addr": "0.0.0.0:0",
                  "bind_device": "eth0",
                  "path_isolation": {
                    "requested": true,
                    "active": true,
                    "method": "so-bindtodevice",
                    "message": "socket is isolated to interface eth0"
                  },
                  "role": "anchor",
                  "score": 980.5,
                  "rtt_ms": 12.4,
                  "jitter_ms": 1.2,
                  "loss_rate": 0.01,
                  "late_rate": 0.02,
                  "queue_depth": 3,
                  "throughput_bps": 12000000,
                  "interface_up": true,
                  "in_cooldown": false
                }
              ],
              "data_packets_sent": 8,
              "duplicate_packets_sent": 4,
              "duplicate_packets_dropped": 4,
              "data_packets_received": 7,
              "fec_packets_sent": 0,
              "fec_packets_recovered": 2,
              "fec_packets_skipped": 3,
              "fec": {
                "configured": true,
                "production_ready": false,
                "message": "FEC is enabled"
              },
              "late_packets_dropped": 1,
              "message": "live"
            }
            """);

        Assert.True(status.Enabled);
        Assert.True(status.Running);
        Assert.Equal(1, status.AnchorPathId);
        Assert.Equal([1], status.Schedule.DataPathIds);
        Assert.Equal([2], status.Schedule.FecPathIds);
        Assert.Equal("running", status.Tunnel.State);
        Assert.Equal("xbond0", status.Tunnel.DeviceName);
        Assert.Equal((ulong)8, status.DataPacketsSent);
        Assert.Equal((ulong)4, status.DuplicatePacketsSent);
        Assert.Equal((ulong)4, status.DuplicatePacketsDropped);
        Assert.Equal((ulong)7, status.DataPacketsReceived);
        Assert.Equal((ulong)2, status.FecPacketsRecovered);
        Assert.Equal((ulong)3, status.FecPacketsSkipped);
        Assert.True(status.Fec.Configured);
        Assert.False(status.Fec.ProductionReady);
        Assert.Equal((ulong)1, status.LatePacketsDropped);

        var path = Assert.Single(status.Paths);
        Assert.Equal("fiber", path.Name);
        Assert.Equal("anchor", path.Role);
        Assert.Equal("0.0.0.0:0", path.BindAddress);
        Assert.Equal("eth0", path.BindDevice);
        Assert.True(path.PathIsolation.Requested);
        Assert.True(path.PathIsolation.Active);
        Assert.Equal(1.2, path.JitterMs);
        Assert.Equal(3, path.QueueDepth);
        Assert.Equal((ulong)12_000_000, path.ThroughputBps);
    }

    [Fact]
    public void ParsePingJson_MapsPublicHeartbeatResult()
    {
        var result = XBondLabService.ParsePingJson(
            """
            {
              "server": "45.77.241.247:8444",
              "bind": "10.202.0.2:34581",
              "path_id": 1,
              "session_id": 1781369695905869,
              "sent": 10,
              "received": 10,
              "lost": 0,
              "loss_rate": 0.0,
              "min_rtt_ms": 46.632,
              "avg_rtt_ms": 59.1474,
              "max_rtt_ms": 126.739,
              "replies": [
                { "sequence": 1, "rtt_ms": 49.213 }
              ]
            }
            """);

        Assert.True(result.Succeeded);
        Assert.Equal("45.77.241.247:8444", result.Server);
        Assert.Equal("10.202.0.2:34581", result.Bind);
        Assert.Equal(10, result.Sent);
        Assert.Equal(10, result.Received);
        Assert.Equal(0, result.Lost);
        Assert.Equal(59.1474, result.AvgRttMs);
        Assert.Equal((ulong)1, Assert.Single(result.Replies).Sequence);
    }

    [Fact]
    public void ParseMultiPingJson_MapsPerPathProbeResultAndRouteVerification()
    {
        var result = XBondLabService.ParseMultiPingJson(
            """
            {
              "mode": "anchor-duplicate-1",
              "anchor_path_id": 1,
              "duplicate_path_ids": [2],
              "started_at": 1781369695905869,
              "completed_at": 1781369696105869,
              "paths": [
                {
                  "path_id": 1,
                  "adapter": "Stable primary",
                  "interface_name": "eth0",
                  "bind": "192.0.2.10:34100",
                  "source": "192.0.2.10",
                  "sent": 10,
                  "received": 10,
                  "acks": 10,
                  "first_arrivals": 10,
                  "duplicates_dropped": null,
                  "loss_rate": 0.0,
                  "avg_rtt_ms": 42.5,
                  "route_verified": true,
                  "route_verification": {
                    "verified": true,
                    "method": "ip-route-get",
                    "reason": "route uses dev eth0 with source 192.0.2.10"
                  }
                },
                {
                  "path_id": 2,
                  "adapter": "Backup modem",
                  "interface_name": "wwan0",
                  "bind": "192.0.2.11:34101",
                  "source": "192.0.2.11",
                  "sent": 10,
                  "received": 10,
                  "acks": 10,
                  "first_arrivals": 0,
                  "duplicates_dropped": 10,
                  "loss_rate": 0.0,
                  "avg_rtt_ms": 60.0,
                  "route_verified": false,
                  "route_verification": {
                    "verified": false,
                    "method": "ip-route-get",
                    "reason": "route leaves through blocked tunnel interface connectify0"
                  }
                }
              ]
            }
            """);

        Assert.True(result.Succeeded);
        Assert.False(result.FullyVerified);
        Assert.True(result.HasRouteWarnings);
        Assert.Equal("anchor-duplicate-1", result.Mode);
        Assert.Equal(1, result.AnchorPathId);
        Assert.Equal([2], result.DuplicatePathIds);
        Assert.Equal(20, result.TotalSent);
        Assert.Equal(20, result.TotalAcks);
        Assert.Equal(10, result.TotalFirstArrivals);
        Assert.Equal(10, result.ExpectedSequences);

        var backup = result.Paths[1];
        Assert.Equal("Backup modem", backup.Adapter);
        Assert.Equal(10, backup.Acks);
        Assert.Equal(10, backup.DuplicatesDropped);
        Assert.False(backup.RouteVerified);
        Assert.Contains("connectify0", backup.RouteVerification.Reason);
    }

    [Fact]
    public void ParseMultiPingJson_DeadBackupPathDoesNotPassOverallProbe()
    {
        var result = XBondLabService.ParseMultiPingJson(
            """
            {
              "mode": "anchor-duplicate-1",
              "anchor_path_id": 1,
              "duplicate_path_ids": [2],
              "started_at": 1781369695905869,
              "completed_at": 1781369696105869,
              "paths": [
                {
                  "path_id": 1,
                  "adapter": "Stable primary",
                  "interface_name": "eth0",
                  "bind": "192.0.2.10:34100",
                  "source": "192.0.2.10",
                  "sent": 10,
                  "received": 10,
                  "acks": 10,
                  "first_arrivals": 10,
                  "duplicates_dropped": 0,
                  "loss_rate": 0.0,
                  "avg_rtt_ms": 42.5,
                  "route_verified": true,
                  "route_verification": {
                    "verified": true,
                    "method": "ip-route-get",
                    "reason": "route uses dev eth0 with source 192.0.2.10"
                  }
                },
                {
                  "path_id": 2,
                  "adapter": "Dead backup",
                  "interface_name": "wwan0",
                  "bind": "192.0.2.11:34101",
                  "source": "192.0.2.11",
                  "sent": 10,
                  "received": 0,
                  "acks": 0,
                  "first_arrivals": 0,
                  "duplicates_dropped": 0,
                  "loss_rate": 1.0,
                  "avg_rtt_ms": null,
                  "route_verified": true,
                  "route_verification": {
                    "verified": true,
                    "method": "ip-route-get",
                    "reason": "route uses dev wwan0 with source 192.0.2.11"
                  }
                }
              ]
            }
            """);

        Assert.False(result.Succeeded);
        Assert.False(result.FullyVerified);
        Assert.True(result.HasPathLoss);
        Assert.True(result.HasAnyPathResponse);
    }

    [Theory]
    [InlineData("1.1.1.1", "1.1.1.1")]
    [InlineData(" 8.8.8.8 ", "8.8.8.8")]
    public void NormalizeTarget_AcceptsSingleIpv4AddressOnly(string input, string expected)
    {
        Assert.Equal(expected, XBondScopedRouteService.NormalizeTarget(input));
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("255.255.255.255")]
    [InlineData("1.1.1.1/32")]
    [InlineData("cloudflare-dns.com")]
    public void NormalizeTarget_RejectsUnsafeScopedRouteTargets(string input)
    {
        Assert.Throws<ArgumentException>(() => XBondScopedRouteService.NormalizeTarget(input));
    }

    [Fact]
    public void ApplyPingOutput_ParsesLinuxPingSummary()
    {
        var result = new XBondScopedRouteTestResult();

        XBondScopedRouteService.ApplyPingOutput(
            result,
            """
            10 packets transmitted, 10 received, 0% packet loss, time 9012ms
            rtt min/avg/max/mdev = 60.660/70.169/101.037/12.360 ms
            """);

        Assert.Equal(10, result.Sent);
        Assert.Equal(10, result.Received);
        Assert.Equal(0, result.Lost);
        Assert.Equal(0, result.LossRate);
        Assert.Equal(60.660, result.MinRttMs);
        Assert.Equal(70.169, result.AvgRttMs);
        Assert.Equal(101.037, result.MaxRttMs);
    }
}

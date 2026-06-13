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
              "duplicate_packets_dropped": 4,
              "fec_packets_recovered": 2,
              "late_packets_dropped": 1,
              "message": "live"
            }
            """);

        Assert.True(status.Enabled);
        Assert.True(status.Running);
        Assert.Equal(1, status.AnchorPathId);
        Assert.Equal([1], status.Schedule.DataPathIds);
        Assert.Equal([2], status.Schedule.FecPathIds);
        Assert.Equal((ulong)4, status.DuplicatePacketsDropped);
        Assert.Equal((ulong)2, status.FecPacketsRecovered);
        Assert.Equal((ulong)1, status.LatePacketsDropped);

        var path = Assert.Single(status.Paths);
        Assert.Equal("fiber", path.Name);
        Assert.Equal("anchor", path.Role);
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
    public void HasBypassPort_TreatsZeroRangeEndAsSinglePort()
    {
        var settings = new StreamingBypassSettings
        {
            Ports =
            [
                new PortRule
                {
                    Port = 8444,
                    PortRangeEnd = 0,
                    Protocol = "udp"
                }
            ]
        };

        Assert.True(XBondLabService.HasBypassPort(settings, 8444, "UDP"));
        Assert.False(XBondLabService.HasBypassPort(settings, 8445, "udp"));
    }
}

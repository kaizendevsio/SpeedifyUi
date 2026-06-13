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
}

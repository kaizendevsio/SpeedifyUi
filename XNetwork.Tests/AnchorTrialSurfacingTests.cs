using System.Text.Json;
using XNetwork.Models;
using XNetwork.Services;

namespace XNetwork.Tests;

public class AnchorTrialSurfacingTests
{
    [Fact]
    public void TrialFieldsDeserializeFromClientStatus()
    {
        const string json = """
        {
          "path_id": 2,
          "name": "starlink",
          "role": "trial",
          "score": 700.0,
          "smoothed_score": 690.0,
          "effective_score": 640.0,
          "flap_penalty": 612.5,
          "trial": {
            "path_id": 2,
            "ticks": 5,
            "success_ticks": 4,
            "mirrored_bytes": 9000000,
            "required_ticks": 20,
            "required_success_ticks": 15
          },
          "loss_rate": 0.0,
          "late_rate": 0.0,
          "queue_depth": 0,
          "throughput_bps": 0,
          "interface_up": true,
          "in_cooldown": false
        }
        """;

        var path = JsonSerializer.Deserialize<XBondPathStatus>(json);

        Assert.NotNull(path);
        Assert.Equal("trial", path!.Role);
        Assert.Equal(640.0, path.EffectiveScore);
        Assert.Equal(612.5, path.FlapPenalty);
        Assert.Equal(4, path.Trial!.SuccessTicks);
        Assert.Equal(15, path.Trial.RequiredSuccessTicks);
    }

    [Fact]
    public void OldClientStatusWithoutNewFieldsStillParses()
    {
        const string json = """
        {
          "path_id": 1,
          "name": "fiber",
          "role": "anchor",
          "score": 800.0,
          "loss_rate": 0.0,
          "late_rate": 0.0,
          "queue_depth": 0,
          "throughput_bps": 0,
          "interface_up": true,
          "in_cooldown": false
        }
        """;

        var path = JsonSerializer.Deserialize<XBondPathStatus>(json);

        Assert.NotNull(path);
        Assert.Equal(0, path!.EffectiveScore);
        Assert.Null(path.Trial);
    }

    [Fact]
    public void SchedulePlanTrialPathIdsDefaultToEmpty()
    {
        const string json = """
        {
          "mode": "anchor-duplicate-1",
          "data_path_ids": [1],
          "duplicate_path_ids": [2],
          "fec_path_ids": []
        }
        """;

        var plan = JsonSerializer.Deserialize<XBondSchedulePlan>(json);

        Assert.NotNull(plan);
        Assert.Empty(plan!.TrialPathIds);
    }

    [Fact]
    public void TrialPathIsFlaggedAndDescribed()
    {
        var path = new XBondPathStatsSnapshot
        {
            Role = "trial",
            InterfaceUp = true,
            IsActive = true,
            Trial = new XBondPathTrialStatus
            {
                PathId = 2,
                Ticks = 5,
                SuccessTicks = 4,
                MirroredBytes = 9_000_000,
                RequiredTicks = 20,
                RequiredSuccessTicks = 15
            }
        };

        Assert.True(path.IsTrial);
        Assert.False(path.IsAnchor);
        Assert.Equal("Testing as anchor (5/20)", path.StateText);
    }

    /// <summary>
    /// Suppression is reported by the client, not recomputed here against a duplicated
    /// threshold: <c>flap_suppress_threshold</c> is operator-configurable, so a hardcoded
    /// copy would drift the moment anyone tuned it.
    /// </summary>
    [Fact]
    public void SuppressionIsTakenFromTheClientNotRecomputed()
    {
        const string json = """
        {
          "path_id": 2, "name": "starlink", "role": "backup", "score": 700.0,
          "flap_penalty": 120.0, "suppressed": true,
          "loss_rate": 0.0, "late_rate": 0.0, "queue_depth": 0, "throughput_bps": 0,
          "interface_up": true, "in_cooldown": false
        }
        """;

        var path = JsonSerializer.Deserialize<XBondPathStatus>(json);

        Assert.NotNull(path);
        // A low penalty with suppressed=true would be impossible under a hardcoded 500
        // threshold, which is exactly why the flag has to come from the client.
        Assert.True(path!.Suppressed);
        Assert.Equal(120.0, path.FlapPenalty);
    }

    /// <summary>
    /// Goes through <see cref="XBondStatusService.ParseRuntimeStatusJson"/> rather than
    /// calling <c>FromStatus</c> directly. The direct call bypasses the role/field
    /// rewriting that the real pipeline performs, which is how a trial path silently
    /// arrived at the dashboard labelled "probe" with every new field zeroed.
    /// </summary>
    [Fact]
    public void TrialSurvivesTheFullRuntimeStatusPipeline()
    {
        const string json = """
        {
          "enabled": true,
          "running": true,
          "mode": "anchor-duplicate-1",
          "redundancy_policy": "balanced",
          "server_addr": "1.2.3.4:8444",
          "anchor_path_id": 1,
          "schedule": {
            "mode": "anchor-duplicate-1",
            "anchor_path_id": 1,
            "data_path_ids": [1],
            "duplicate_path_ids": [3],
            "fec_path_ids": [],
            "trial_path_ids": [2]
          },
          "paths": [
            {
              "path_id": 1, "name": "fiber", "interface_name": "enx1",
              "loss_rate": 0.0, "late_rate": 0.0, "queue_depth": 0, "throughput_bps": 1000,
              "interface_up": true, "in_cooldown": false
            },
            {
              "path_id": 2, "name": "starlink", "interface_name": "enx2",
              "role_reason": "On trial as anchor candidate: 5/20 ticks, 4/15 clean under load.",
              "loss_rate": 0.0, "late_rate": 0.0, "queue_depth": 0, "throughput_bps": 1000,
              "interface_up": true, "in_cooldown": false
            },
            {
              "path_id": 3, "name": "smart", "interface_name": "enx3",
              "loss_rate": 0.0, "late_rate": 0.0, "queue_depth": 0,
              "throughput_bps": 1000, "interface_up": true, "in_cooldown": false
            }
          ],
          "anchor_stability": [
            { "path_id": 1, "smoothed_score": 800.0, "effective_score": 795.0 },
            {
              "path_id": 2, "smoothed_score": 890.0, "effective_score": 640.0,
              "flap_penalty": 612.5, "suppressed": false,
              "trial": {
                "path_id": 2, "ticks": 5, "success_ticks": 4, "mirrored_bytes": 9000000,
                "required_ticks": 20, "required_success_ticks": 15
              }
            },
            { "path_id": 3, "smoothed_score": 700.0, "effective_score": 700.0 }
          ],
          "data_packets_sent": 0, "duplicate_packets_sent": 0, "duplicate_packets_dropped": 0,
          "data_packets_received": 0, "data_bytes_sent": 0, "data_bytes_received": 0,
          "outbound_throughput_bps": 0, "inbound_throughput_bps": 0,
          "fec_packets_sent": 0, "fec_packets_recovered": 0, "fec_packets_skipped": 0,
          "late_packets_dropped": 0, "message": ""
        }
        """;

        var status = XBondStatusService.ParseRuntimeStatusJson(
            json,
            new XBondSettings(),
            DateTime.UtcNow);

        var trial = status.Paths.Single(path => path.PathId == 2);
        Assert.Equal("trial", trial.Role);
        Assert.Equal(640.0, trial.EffectiveScore);
        Assert.Equal(612.5, trial.FlapPenalty);
        Assert.NotNull(trial.Trial);
        Assert.Equal(5, trial.Trial!.Ticks);

        // The real backup must keep its own role rather than being displaced by the trial.
        Assert.Equal("backup", status.Paths.Single(path => path.PathId == 3).Role);
        Assert.Equal("anchor", status.Paths.Single(path => path.PathId == 1).Role);
    }

    [Fact]
    public void TrialPathCountsAsActiveAndKeepsItsTrialRole()
    {
        var status = new XBondStatus
        {
            Schedule = new XBondSchedulePlan
            {
                AnchorPathId = 1,
                DataPathIds = [1],
                DuplicatePathIds = [],
                FecPathIds = [],
                TrialPathIds = [2]
            },
            Paths =
            [
                new XBondPathStatus
                {
                    PathId = 1,
                    InterfaceName = "enx1",
                    Role = "anchor",
                    InterfaceUp = true
                },
                new XBondPathStatus
                {
                    PathId = 2,
                    InterfaceName = "enx2",
                    Role = "trial",
                    InterfaceUp = true,
                    FlapPenalty = 0,
                    EffectiveScore = 640,
                    Trial = new XBondPathTrialStatus
                    {
                        PathId = 2,
                        Ticks = 5,
                        RequiredTicks = 20,
                        SuccessTicks = 5,
                        RequiredSuccessTicks = 15
                    }
                }
            ]
        };

        var snapshot = XBondStatsService.FromStatus(
            status,
            [],
            new Dictionary<string, F50ModemTelemetry>(StringComparer.OrdinalIgnoreCase),
            [],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            [],
            []);

        var trial = snapshot.Paths.Single(path => path.PathId == 2);
        Assert.True(trial.IsTrial);
        Assert.True(trial.IsActive, "a trial path is carrying mirrored traffic");
        Assert.Equal(5, trial.Trial!.Ticks);
        Assert.Equal(640, trial.EffectiveScore);
        Assert.Contains(snapshot.ActivePaths, path => path.PathId == 2);
    }
}

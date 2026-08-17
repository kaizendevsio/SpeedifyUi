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

    [Fact]
    public void SuppressedPathIsFlaggedFromItsFlapPenalty()
    {
        var suppressed = new XBondPathStatsSnapshot { InterfaceUp = true, FlapPenalty = 900 };
        var trusted = new XBondPathStatsSnapshot { InterfaceUp = true, FlapPenalty = 100 };

        Assert.True(suppressed.IsSuppressed);
        Assert.False(trusted.IsSuppressed);
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

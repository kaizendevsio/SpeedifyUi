using System.Text.Json;
using System.Text.Json.Serialization;
using XNetwork.Models;

namespace XNetwork.Services;

public class XBondStatusService(ILogger<XBondStatusService> logger, XBondSettings settings)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public XBondSettings Settings => settings;

    public static XBondStatus ParseStatusJson(string json)
    {
        var status = JsonSerializer.Deserialize<XBondStatus>(json, JsonOptions)
            ?? throw new JsonException("uLink status JSON was empty");
        status.UpdatedAtUtc = DateTime.UtcNow;
        return status;
    }

    public static XBondStatus ParseRuntimeStatusJson(string json, XBondSettings settings)
        => ParseRuntimeStatusJson(json, settings, DateTime.UtcNow);

    public static XBondStatus ParseRuntimeStatusJson(
        string json,
        XBondSettings settings,
        DateTime updatedAtUtc)
    {
        var runtime = JsonSerializer.Deserialize<XBondRuntimeStatusDocument>(json, JsonOptions)
            ?? throw new JsonException("uLink runtime status JSON was empty");

        return FromRuntimeStatus(runtime, settings, updatedAtUtc);
    }

    public async Task<XBondStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (!settings.Enabled)
        {
            return DisabledStatus("uLink runtime is disabled. Enable xbond-client on this host.");
        }

        try
        {
            var path = string.IsNullOrWhiteSpace(settings.RuntimeStatusPath)
                ? "/run/xbond/client-status.json"
                : settings.RuntimeStatusPath;
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            var updatedAtUtc = File.GetLastWriteTimeUtc(path);
            return ParseRuntimeStatusJson(json, settings, updatedAtUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(ex, "Failed to read uLink runtime status file {Path}", settings.RuntimeStatusPath);
            return ErrorStatus(ex.Message);
        }
    }

    private static XBondStatus FromRuntimeStatus(
        XBondRuntimeStatusDocument runtime,
        XBondSettings settings,
        DateTime updatedAtUtc)
    {
        var mode = string.IsNullOrWhiteSpace(runtime.Mode) ? settings.ScheduleMode : runtime.Mode;
        var paths = SelectPathRoles(runtime.Paths, settings.MaxActiveBackups);
        var schedule = runtime.Schedule ?? BuildSchedule(mode, paths);
        ApplyRuntimeScheduleRoles(paths, schedule);

        return new XBondStatus
        {
            Enabled = true,
            Running = runtime.Running,
            Mode = mode,
            RedundancyPolicy = string.IsNullOrWhiteSpace(runtime.RedundancyPolicy)
                ? "balanced"
                : runtime.RedundancyPolicy,
            ServerAddress = string.IsNullOrWhiteSpace(runtime.ServerAddress)
                ? settings.PublicTestServerAddress
                : runtime.ServerAddress,
            Tunnel = runtime.Tunnel ?? new XBondTunnelStatus(),
            AnchorPathId = schedule.AnchorPathId,
            Schedule = schedule,
            Paths = paths,
            DataPacketsSent = runtime.DataPacketsSent,
            DuplicatePacketsSent = runtime.DuplicatePacketsSent,
            DuplicatePacketsDropped = runtime.DuplicatePacketsDropped,
            DataPacketsReceived = runtime.DataPacketsReceived,
            DataBytesSent = runtime.DataBytesSent,
            DataBytesReceived = runtime.DataBytesReceived,
            OutboundThroughputBps = runtime.OutboundThroughputBps,
            InboundThroughputBps = runtime.InboundThroughputBps,
            FecPacketsSent = runtime.FecPacketsSent,
            FecPacketsRecovered = runtime.FecPacketsRecovered,
            FecPacketsSkipped = runtime.FecPacketsSkipped,
            Fec = runtime.Fec ?? new XBondFecStatus(),
            LatePacketsDropped = runtime.LatePacketsDropped,
            Reorder = runtime.Reorder ?? new XBondReorderStatus(),
            Process = runtime.Process ?? new XBondProcessStatus(),
            ServerRecovery = runtime.ServerRecovery ?? new XBondServerRecoveryStatus(),
            ServerHealth = runtime.ServerHealth ?? runtime.ServerRecovery?.ServerHealth ?? new XBondServerHealthStatus(),
            Recovery = runtime.Recovery ?? new XBondRecoveryStatus(),
            Message = string.IsNullOrWhiteSpace(runtime.Message)
                ? "uLink runtime status loaded."
                : runtime.Message,
            UpdatedAtUtc = updatedAtUtc
        };
    }

    private static List<XBondPathStatus> SelectPathRoles(IEnumerable<XBondPathStatus> paths, int maxBackups)
    {
        var scored = paths
            .Select(ClonePath)
            .Select(path =>
            {
                path.Score = ScorePath(path);
                path.Role = !path.InterfaceUp
                    ? "unavailable"
                    : path.InCooldown || path.HeartbeatFailed
                        ? "cooldown"
                        : "probe";
                return path;
            })
            .OrderByDescending(path => path.Score)
            .ToList();

        var anchorAssigned = false;
        var backupsAssigned = 0;
        var backupLimit = Math.Max(0, maxBackups);
        foreach (var path in scored)
        {
            if (!IsRealtimeEligible(path) || !double.IsFinite(path.Score))
            {
                continue;
            }

            if (!anchorAssigned)
            {
                path.Role = "anchor";
                anchorAssigned = true;
            }
            else if (backupsAssigned < backupLimit && path.Score > -100_000)
            {
                path.Role = "backup";
                backupsAssigned++;
            }
        }

        return scored;
    }

    private static XBondSchedulePlan BuildSchedule(string mode, IReadOnlyCollection<XBondPathStatus> paths)
    {
        var anchorId = paths.FirstOrDefault(path => string.Equals(path.Role, "anchor", StringComparison.OrdinalIgnoreCase))?.PathId;
        var backups = paths
            .Where(path => string.Equals(path.Role, "backup", StringComparison.OrdinalIgnoreCase))
            .Select(path => path.PathId)
            .ToList();

        var normalizedMode = string.IsNullOrWhiteSpace(mode) ? "anchor-duplicate-1" : mode;
        return normalizedMode.ToLowerInvariant() switch
        {
            "anchor-only" => new XBondSchedulePlan
            {
                Mode = normalizedMode,
                AnchorPathId = anchorId,
                DataPathIds = anchorId.HasValue ? [anchorId.Value] : []
            },
            "anchor-fec" => new XBondSchedulePlan
            {
                Mode = normalizedMode,
                AnchorPathId = anchorId,
                DataPathIds = anchorId.HasValue ? [anchorId.Value] : [],
                FecPathIds = backups
            },
            "full-duplicate-debug" => new XBondSchedulePlan
            {
                Mode = normalizedMode,
                AnchorPathId = anchorId,
                DataPathIds = anchorId.HasValue ? [anchorId.Value] : [],
                DuplicatePathIds = backups
            },
            _ => new XBondSchedulePlan
            {
                Mode = "anchor-duplicate-1",
                AnchorPathId = anchorId,
                DataPathIds = anchorId.HasValue ? [anchorId.Value] : [],
                DuplicatePathIds = backups.Take(1).ToList()
            }
        };
    }

    private static void ApplyRuntimeScheduleRoles(
        IReadOnlyCollection<XBondPathStatus> paths,
        XBondSchedulePlan schedule)
    {
        var hasSchedule = schedule.AnchorPathId.HasValue ||
                          schedule.DataPathIds.Count > 0 ||
                          schedule.DuplicatePathIds.Count > 0 ||
                          schedule.FecPathIds.Count > 0 ||
                          schedule.TrialPathIds.Count > 0;
        if (!hasSchedule)
        {
            return;
        }

        var activeBackupIds = schedule.DuplicatePathIds
            .Concat(schedule.FecPathIds)
            .ToHashSet();
        var trialIds = schedule.TrialPathIds.ToHashSet();

        foreach (var path in paths)
        {
            if (!path.InterfaceUp)
            {
                path.Role = "unavailable";
            }
            else if (path.InCooldown || path.HeartbeatFailed || !string.IsNullOrWhiteSpace(path.DemotionReason))
            {
                path.Role = "cooldown";
            }
            else if (schedule.AnchorPathId == path.PathId)
            {
                path.Role = "anchor";
            }
            // Checked before backup: a path under trial is carrying mirrored traffic to
            // earn the anchor role, which is what the operator needs to see. Without this
            // the client's "trial" role was silently rewritten to "probe" here and every
            // trial affordance on the dashboard became unreachable.
            else if (trialIds.Contains(path.PathId))
            {
                path.Role = "trial";
            }
            else if (activeBackupIds.Contains(path.PathId))
            {
                path.Role = "backup";
            }
            else
            {
                path.Role = "probe";
            }
        }
    }

    private static bool IsRealtimeEligible(XBondPathStatus path) =>
        path.InterfaceUp &&
        !path.InCooldown &&
        !path.HeartbeatFailed &&
        !path.HeartbeatWarmingUp &&
        path.HeartbeatSampleCount > 0;

    private static double ScorePath(XBondPathStatus path)
    {
        if (!IsRealtimeEligible(path))
        {
            return -1_000_000;
        }

        var rttPenalty = Math.Min(path.RttMs ?? 500, 2_000) * 2.0;
        var jitterPenalty = Math.Min(path.JitterMs ?? 100, 1_000) * 2.5;
        var effectiveLoss = path.HeartbeatWarmingUp && !path.HeartbeatFailed ? 0.0 : path.LossRate;
        var lossPenalty = Math.Clamp(effectiveLoss, 0.0, 1.0) * 800.0;
        var latePenalty = Math.Clamp(path.LateRate, 0.0, 1.0) * 1_000.0;
        var queuePenalty = Math.Min(path.QueueDepth, 10_000) * 0.1;
        var throughputBonus = path.ThroughputBps == 0
            ? 0
            : Math.Min(Math.Log10(path.ThroughputBps), 9.0) * 10.0;

        return 1_000.0 - rttPenalty - jitterPenalty - lossPenalty - latePenalty - queuePenalty + throughputBonus;
    }

    private static XBondPathStatus ClonePath(XBondPathStatus path)
    {
        return new XBondPathStatus
        {
            PathId = path.PathId,
            Name = path.Name,
            InterfaceName = path.InterfaceName,
            BindAddress = path.BindAddress,
            BindDevice = path.BindDevice,
            PathIsolation = path.PathIsolation,
            Role = path.Role,
            Score = path.Score,
            SmoothedScore = path.SmoothedScore,
            EffectiveScore = path.EffectiveScore,
            FlapPenalty = path.FlapPenalty,
            Suppressed = path.Suppressed,
            Trial = path.Trial,
            RttMs = path.RttMs,
            JitterMs = path.JitterMs,
            LossRate = path.LossRate,
            LateRate = path.LateRate,
            QueueDepth = path.QueueDepth,
            ThroughputBps = path.ThroughputBps,
            OutboundThroughputBps = path.OutboundThroughputBps,
            InboundThroughputBps = path.InboundThroughputBps,
            DuplicateInboundThroughputBps = path.DuplicateInboundThroughputBps,
            RawInboundThroughputBps = path.RawInboundThroughputBps,
            InterfaceUp = path.InterfaceUp,
            InCooldown = path.InCooldown,
            SendFailureStreak = path.SendFailureStreak,
            StaleAckMs = path.StaleAckMs,
            QueuePressure = path.QueuePressure,
            DuplicateUsefulness = path.DuplicateUsefulness,
            ThroughputCollapseScore = path.ThroughputCollapseScore,
            DemotionReason = path.DemotionReason,
            RoleReason = path.RoleReason,
            SocketGeneration = path.SocketGeneration,
            SocketIfindex = path.SocketIfindex,
            SocketBindAddress = path.SocketBindAddress,
            LastSocketError = path.LastSocketError,
            LastRebindReason = path.LastRebindReason,
            LastRebindError = path.LastRebindError,
            LastRebindAtMicros = path.LastRebindAtMicros,
            RebindCount = path.RebindCount,
            PendingProbes = path.PendingProbes,
            HeartbeatSampleCount = path.HeartbeatSampleCount,
            HeartbeatConsecutiveMisses = path.HeartbeatConsecutiveMisses,
            HeartbeatConsecutiveSuccesses = path.HeartbeatConsecutiveSuccesses,
            HeartbeatWarmingUp = path.HeartbeatWarmingUp,
            HeartbeatFailed = path.HeartbeatFailed
        };
    }

    private static XBondStatus DisabledStatus(string message)
    {
        return new XBondStatus
        {
            Enabled = false,
            Running = false,
            Mode = "anchor-duplicate-1",
            RedundancyPolicy = "balanced",
            Message = message,
            UpdatedAtUtc = DateTime.UtcNow
        };
    }

    private static XBondStatus ErrorStatus(string error)
    {
        return new XBondStatus
        {
            Enabled = true,
            Running = false,
            Mode = "anchor-duplicate-1",
            RedundancyPolicy = "balanced",
            Message = "uLink runtime status is unavailable",
            Error = error,
            UpdatedAtUtc = DateTime.UtcNow
        };
    }

    private sealed class XBondRuntimeStatusDocument
    {
        [JsonPropertyName("running")]
        public bool Running { get; set; }

        [JsonPropertyName("mode")]
        public string Mode { get; set; } = "anchor-duplicate-1";

        [JsonPropertyName("redundancy_policy")]
        public string RedundancyPolicy { get; set; } = "balanced";

        [JsonPropertyName("server_addr")]
        public string ServerAddress { get; set; } = "";

        [JsonPropertyName("tunnel")]
        public XBondTunnelStatus? Tunnel { get; set; }

        [JsonPropertyName("schedule")]
        public XBondSchedulePlan? Schedule { get; set; }

        [JsonPropertyName("paths")]
        public List<XBondPathStatus> Paths { get; set; } = new();

        [JsonPropertyName("data_packets_sent")]
        public ulong DataPacketsSent { get; set; }

        [JsonPropertyName("duplicate_packets_sent")]
        public ulong DuplicatePacketsSent { get; set; }

        [JsonPropertyName("duplicate_packets_dropped")]
        public ulong DuplicatePacketsDropped { get; set; }

        [JsonPropertyName("data_packets_received")]
        public ulong DataPacketsReceived { get; set; }

        [JsonPropertyName("data_bytes_sent")]
        public ulong DataBytesSent { get; set; }

        [JsonPropertyName("data_bytes_received")]
        public ulong DataBytesReceived { get; set; }

        [JsonPropertyName("outbound_throughput_bps")]
        public ulong OutboundThroughputBps { get; set; }

        [JsonPropertyName("inbound_throughput_bps")]
        public ulong InboundThroughputBps { get; set; }

        [JsonPropertyName("fec_packets_sent")]
        public ulong FecPacketsSent { get; set; }

        [JsonPropertyName("fec_packets_recovered")]
        public ulong FecPacketsRecovered { get; set; }

        [JsonPropertyName("fec_packets_skipped")]
        public ulong FecPacketsSkipped { get; set; }

        [JsonPropertyName("fec")]
        public XBondFecStatus? Fec { get; set; }

        [JsonPropertyName("late_packets_dropped")]
        public ulong LatePacketsDropped { get; set; }

        [JsonPropertyName("reorder")]
        public XBondReorderStatus? Reorder { get; set; }

        [JsonPropertyName("process")]
        public XBondProcessStatus? Process { get; set; }

        [JsonPropertyName("server_recovery")]
        public XBondServerRecoveryStatus? ServerRecovery { get; set; }

        [JsonPropertyName("server_health")]
        public XBondServerHealthStatus? ServerHealth { get; set; }

        [JsonPropertyName("recovery")]
        public XBondRecoveryStatus? Recovery { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }
}

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
            ?? throw new JsonException("XBond status JSON was empty");
        status.UpdatedAtUtc = DateTime.UtcNow;
        return status;
    }

    public static XBondStatus ParseRuntimeStatusJson(string json, XBondSettings settings)
    {
        var runtime = JsonSerializer.Deserialize<XBondRuntimeStatusDocument>(json, JsonOptions)
            ?? throw new JsonException("XBond runtime status JSON was empty");

        return FromRuntimeStatus(runtime, settings);
    }

    public async Task<XBondStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (!settings.Enabled)
        {
            return DisabledStatus("XBond runtime is disabled. Enable xbond-client on this host.");
        }

        try
        {
            var path = string.IsNullOrWhiteSpace(settings.RuntimeStatusPath)
                ? "/run/xbond/client-status.json"
                : settings.RuntimeStatusPath;
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return ParseRuntimeStatusJson(json, settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(ex, "Failed to read XBond runtime status file {Path}", settings.RuntimeStatusPath);
            return ErrorStatus(ex.Message);
        }
    }

    private static XBondStatus FromRuntimeStatus(XBondRuntimeStatusDocument runtime, XBondSettings settings)
    {
        var mode = string.IsNullOrWhiteSpace(runtime.Mode) ? settings.ScheduleMode : runtime.Mode;
        var paths = SelectPathRoles(runtime.Paths, settings.MaxActiveBackups);
        var schedule = BuildSchedule(mode, paths);

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
            Message = string.IsNullOrWhiteSpace(runtime.Message)
                ? "XBond runtime status loaded."
                : runtime.Message,
            UpdatedAtUtc = DateTime.UtcNow
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
                    : path.InCooldown || path.LossRate >= 1.0
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

    private static bool IsRealtimeEligible(XBondPathStatus path) =>
        path.InterfaceUp && !path.InCooldown && path.LossRate < 1.0;

    private static double ScorePath(XBondPathStatus path)
    {
        if (!IsRealtimeEligible(path))
        {
            return -1_000_000;
        }

        var rttPenalty = Math.Min(path.RttMs ?? 500, 2_000) * 2.0;
        var jitterPenalty = Math.Min(path.JitterMs ?? 100, 1_000) * 2.5;
        var lossPenalty = Math.Clamp(path.LossRate, 0.0, 1.0) * 800.0;
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
            InCooldown = path.InCooldown
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
            Message = "XBond runtime status is unavailable",
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

        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }
}

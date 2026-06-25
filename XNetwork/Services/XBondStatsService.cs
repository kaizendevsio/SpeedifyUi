using XNetwork.Models;

namespace XNetwork.Services;

public interface IXBondStatsProvider
{
    Task<XBondStatsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
}

public sealed class XBondStatsService(
    XBondStatusService statusService,
    InterfaceMetadataService interfaceMetadataService,
    F50ModemTelemetryService f50TelemetryService) : IXBondStatsProvider
{
    private const ulong StaleRttAckAgeMs = 5_000;

    public async Task<XBondStatsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var status = await statusService.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var interfaces = await interfaceMetadataService.GetInterfacesAsync(cancellationToken).ConfigureAwait(false);
        var modemTelemetry = await f50TelemetryService.GetTelemetryByInterfaceAsync(cancellationToken).ConfigureAwait(false);
        return FromStatus(status, interfaces, modemTelemetry);
    }

    public static XBondStatsSnapshot FromStatus(XBondStatus status)
    {
        return FromStatus(status, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
    }

    public static XBondStatsSnapshot FromStatus(
        XBondStatus status,
        IReadOnlyDictionary<string, string> interfaceDisplayNames)
    {
        return FromStatus(status, interfaceDisplayNames, []);
    }

    public static XBondStatsSnapshot FromStatus(
        XBondStatus status,
        IReadOnlyList<InterfaceMetadataService.InterfaceMetadata> interfaces)
    {
        return FromStatus(
            status,
            interfaces,
            new Dictionary<string, F50ModemTelemetry>(StringComparer.OrdinalIgnoreCase));
    }

    public static XBondStatsSnapshot FromStatus(
        XBondStatus status,
        IReadOnlyList<InterfaceMetadataService.InterfaceMetadata> interfaces,
        IReadOnlyDictionary<string, F50ModemTelemetry> modemTelemetry)
    {
        var interfaceDisplayNames = interfaces
            .Where(item => !string.IsNullOrWhiteSpace(item.DisplayName))
            .ToDictionary(item => item.Device, item => item.DisplayName, StringComparer.OrdinalIgnoreCase);

        return FromStatus(status, interfaceDisplayNames, interfaces, modemTelemetry);
    }

    private static XBondStatsSnapshot FromStatus(
        XBondStatus status,
        IReadOnlyDictionary<string, string> interfaceDisplayNames,
        IReadOnlyList<InterfaceMetadataService.InterfaceMetadata> interfaces)
    {
        return FromStatus(
            status,
            interfaceDisplayNames,
            interfaces,
            new Dictionary<string, F50ModemTelemetry>(StringComparer.OrdinalIgnoreCase));
    }

    private static XBondStatsSnapshot FromStatus(
        XBondStatus status,
        IReadOnlyDictionary<string, string> interfaceDisplayNames,
        IReadOnlyList<InterfaceMetadataService.InterfaceMetadata> interfaces,
        IReadOnlyDictionary<string, F50ModemTelemetry> modemTelemetry)
    {
        var activeIds = status.Schedule.DataPathIds
            .Concat(status.Schedule.DuplicatePathIds)
            .Concat(status.Schedule.FecPathIds)
            .ToHashSet();

        var configuredPaths = status.Paths
            .Select(path =>
            {
                var lossPercent = path.LossRate * 100;
                var isStaleRtt = IsStaleRtt(path, lossPercent);
                var interfaceName = path.InterfaceName ?? path.BindDevice ?? $"path-{path.PathId}";
                modemTelemetry.TryGetValue(interfaceName, out var cellular);
                return new XBondPathStatsSnapshot
                {
                    PathId = path.PathId,
                    InterfaceName = interfaceName,
                    Name = ResolvePathName(path, interfaceDisplayNames),
                    Role = path.Role,
                    InterfaceUp = path.InterfaceUp,
                    InCooldown = path.InCooldown,
                    DemotionReason = path.DemotionReason,
                    RoleReason = path.RoleReason,
                    SendFailureStreak = path.SendFailureStreak,
                    StaleAckMs = path.StaleAckMs,
                    QueuePressure = path.QueuePressure,
                    DuplicateUsefulness = path.DuplicateUsefulness,
                    ThroughputCollapseScore = path.ThroughputCollapseScore,
                    Score = path.Score,
                    RttMs = isStaleRtt ? null : path.RttMs,
                    JitterMs = isStaleRtt ? null : path.JitterMs,
                    LossPercent = lossPercent,
                    LatePercent = path.LateRate * 100,
                    QueueDepth = path.QueueDepth,
                    ThroughputBps = path.ThroughputBps,
                    OutboundThroughputBps = path.OutboundThroughputBps,
                    InboundThroughputBps = path.InboundThroughputBps,
                    DuplicateInboundThroughputBps = path.DuplicateInboundThroughputBps,
                    RawInboundThroughputBps = path.RawInboundThroughputBps,
                    BindAddress = path.BindAddress ?? "",
                    BindDevice = path.BindDevice ?? "",
                    CellularGeneration = cellular?.Generation,
                    CellularSignalBars = cellular?.SignalBars,
                    IsActive = activeIds.Contains(path.PathId),
                    IsConfigured = true
                };
            });

        var configuredInterfaceNames = configuredPaths
            .Select(path => path.InterfaceName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var nextSyntheticPathId = -1;
        var localInterfaces = interfaces
            .Where(item => item.IsDashboardCandidate && !configuredInterfaceNames.Contains(item.Device))
            .Select(item => new XBondPathStatsSnapshot
            {
                PathId = nextSyntheticPathId--,
                InterfaceName = item.Device,
                Name = string.IsNullOrWhiteSpace(item.DisplayName) ? item.Device : item.DisplayName,
                Role = "standby",
                InterfaceUp = true,
                CellularGeneration = modemTelemetry.TryGetValue(item.Device, out var cellular) ? cellular.Generation : null,
                CellularSignalBars = cellular?.SignalBars,
                IsConfigured = false
            });

        var paths = configuredPaths
            .Concat(localInterfaces)
            .OrderBy(path => path.IsActive ? 0 : 1)
            .ThenBy(path => path.IsAnchor ? 0 : 1)
            .ThenBy(path => path.InterfaceUp ? 0 : 1)
            .ThenBy(path => path.RttMs ?? double.MaxValue)
            .ThenBy(path => path.PathId)
            .ToArray();

        return new XBondStatsSnapshot
        {
            RawStatus = status,
            Paths = paths
        };
    }

    private static string ResolvePathName(
        XBondPathStatus path,
        IReadOnlyDictionary<string, string> interfaceDisplayNames)
    {
        var interfaceName = path.InterfaceName ?? path.BindDevice;
        if (!string.IsNullOrWhiteSpace(interfaceName) &&
            interfaceDisplayNames.TryGetValue(interfaceName, out var displayName) &&
            !string.IsNullOrWhiteSpace(displayName))
        {
            return displayName;
        }

        return string.IsNullOrWhiteSpace(path.Name) ? $"Path {path.PathId}" : path.Name;
    }

    private static bool IsStaleRtt(XBondPathStatus path, double lossPercent)
    {
        if (!path.RttMs.HasValue)
        {
            return false;
        }

        if (!path.InterfaceUp || lossPercent >= 99.5)
        {
            return true;
        }

        return path.StaleAckMs is >= StaleRttAckAgeMs;
    }
}

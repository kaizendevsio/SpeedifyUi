using XNetwork.Models;

namespace XNetwork.Services;

public interface IXBondStatsProvider
{
    Task<XBondStatsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
}

public sealed class XBondStatsService(
    XBondStatusService statusService,
    InterfaceMetadataService interfaceMetadataService) : IXBondStatsProvider
{
    public async Task<XBondStatsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var status = await statusService.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var interfaces = await interfaceMetadataService.GetInterfacesAsync(cancellationToken).ConfigureAwait(false);
        return FromStatus(status, interfaces);
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
        var interfaceDisplayNames = interfaces
            .Where(item => !string.IsNullOrWhiteSpace(item.DisplayName))
            .ToDictionary(item => item.Device, item => item.DisplayName, StringComparer.OrdinalIgnoreCase);

        return FromStatus(status, interfaceDisplayNames, interfaces);
    }

    private static XBondStatsSnapshot FromStatus(
        XBondStatus status,
        IReadOnlyDictionary<string, string> interfaceDisplayNames,
        IReadOnlyList<InterfaceMetadataService.InterfaceMetadata> interfaces)
    {
        var activeIds = status.Schedule.DataPathIds
            .Concat(status.Schedule.DuplicatePathIds)
            .Concat(status.Schedule.FecPathIds)
            .ToHashSet();

        var configuredPaths = status.Paths
            .Select(path => new XBondPathStatsSnapshot
            {
                PathId = path.PathId,
                InterfaceName = path.InterfaceName ?? path.BindDevice ?? $"path-{path.PathId}",
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
                RttMs = path.RttMs,
                JitterMs = path.JitterMs,
                LossPercent = path.LossRate * 100,
                LatePercent = path.LateRate * 100,
                QueueDepth = path.QueueDepth,
                ThroughputBps = path.ThroughputBps,
                OutboundThroughputBps = path.OutboundThroughputBps,
                InboundThroughputBps = path.InboundThroughputBps,
                DuplicateInboundThroughputBps = path.DuplicateInboundThroughputBps,
                RawInboundThroughputBps = path.RawInboundThroughputBps,
                BindAddress = path.BindAddress ?? "",
                BindDevice = path.BindDevice ?? "",
                IsActive = activeIds.Contains(path.PathId),
                IsConfigured = true
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
}

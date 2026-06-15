using XNetwork.Models;

namespace XNetwork.Services;

public sealed class XBondStatsService(
    XBondStatusService statusService,
    InterfaceMetadataService interfaceMetadataService)
{
    public async Task<XBondStatsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var status = await statusService.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var interfaceDisplayNames = await interfaceMetadataService.GetDisplayNamesAsync(cancellationToken).ConfigureAwait(false);
        return FromStatus(status, interfaceDisplayNames);
    }

    public static XBondStatsSnapshot FromStatus(XBondStatus status)
    {
        return FromStatus(status, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
    }

    public static XBondStatsSnapshot FromStatus(
        XBondStatus status,
        IReadOnlyDictionary<string, string> interfaceDisplayNames)
    {
        var activeIds = status.Schedule.DataPathIds
            .Concat(status.Schedule.DuplicatePathIds)
            .Concat(status.Schedule.FecPathIds)
            .ToHashSet();

        var paths = status.Paths
            .Select(path => new XBondPathStatsSnapshot
            {
                PathId = path.PathId,
                InterfaceName = path.InterfaceName ?? path.BindDevice ?? $"path-{path.PathId}",
                Name = ResolvePathName(path, interfaceDisplayNames),
                Role = path.Role,
                InterfaceUp = path.InterfaceUp,
                InCooldown = path.InCooldown,
                Score = path.Score,
                RttMs = path.RttMs,
                JitterMs = path.JitterMs,
                LossPercent = path.LossRate * 100,
                LatePercent = path.LateRate * 100,
                QueueDepth = path.QueueDepth,
                ThroughputBps = path.ThroughputBps,
                BindAddress = path.BindAddress ?? "",
                BindDevice = path.BindDevice ?? "",
                IsActive = activeIds.Contains(path.PathId)
            })
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

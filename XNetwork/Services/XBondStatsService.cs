using XNetwork.Models;

namespace XNetwork.Services;

public sealed class XBondStatsService(XBondStatusService statusService)
{
    public async Task<XBondStatsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var status = await statusService.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        return FromStatus(status);
    }

    public static XBondStatsSnapshot FromStatus(XBondStatus status)
    {
        var activeIds = status.Schedule.DataPathIds
            .Concat(status.Schedule.DuplicatePathIds)
            .Concat(status.Schedule.FecPathIds)
            .ToHashSet();

        var paths = status.Paths
            .Select(path => new XBondPathStatsSnapshot
            {
                PathId = path.PathId,
                Name = string.IsNullOrWhiteSpace(path.Name) ? $"Path {path.PathId}" : path.Name,
                InterfaceName = path.InterfaceName ?? path.BindDevice ?? $"path-{path.PathId}",
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
}

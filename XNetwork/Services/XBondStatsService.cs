using XNetwork.Models;

namespace XNetwork.Services;

public interface IXBondStatsProvider
{
    Task<XBondStatsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
}

public sealed class XBondStatsService(
    XBondStatusService statusService,
    InterfaceMetadataService interfaceMetadataService,
    F50ModemTelemetryService f50TelemetryService,
    NetworkMonitorService networkMonitorService,
    AdapterIdentityService adapterIdentityService) : IXBondStatsProvider
{
    private const ulong StaleRttAckAgeMs = 5_000;

    private static readonly IReadOnlyDictionary<string, string> EmptyNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public async Task<XBondStatsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var status = await statusService.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var interfaces = await interfaceMetadataService.GetInterfacesAsync(cancellationToken).ConfigureAwait(false);
        var gatewayRoutes = await interfaceMetadataService.GetDefaultGatewayRoutesAsync(cancellationToken).ConfigureAwait(false);
        var modemTelemetry = await f50TelemetryService.GetTelemetryByInterfaceAsync(cancellationToken).ConfigureAwait(false);
        return FromStatus(
            status,
            interfaces,
            modemTelemetry,
            gatewayRoutes,
            // Read aliases from the running watchdog, not the startup settings singleton:
            // NetworkMonitorService keeps its own copy, so saved aliases only reach that copy.
            BuildAdapterAliases(networkMonitorService.GetSettings()),
            adapterIdentityService.GetDisplayNames());
    }

    /// <summary>
    /// Manual Link Watchdog aliases keyed by interface. Kept separate from NetworkManager metadata
    /// names so <see cref="AdapterNameResolver"/> can place the discovered ISP name between them.
    /// Read straight from settings so configured uLink paths are covered even when NetworkManager
    /// metadata is unavailable.
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildAdapterAliases(NetworkMonitorSettings settings)
    {
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (interfaceName, alias) in settings.AdapterAliases ?? [])
        {
            if (!string.IsNullOrWhiteSpace(interfaceName) && !string.IsNullOrWhiteSpace(alias))
            {
                aliases[interfaceName] = alias.Trim();
            }
        }

        return aliases;
    }

    public static IReadOnlyList<InterfaceMetadataService.InterfaceMetadata> ApplyAdapterAliases(
        IReadOnlyList<InterfaceMetadataService.InterfaceMetadata> interfaces,
        NetworkMonitorSettings settings) => interfaces
        .Select(item => NetworkMonitorSettingsStore.GetAdapterAlias(settings, item.Device) is { } alias
            ? item with { DisplayName = alias }
            : item)
        .ToArray();

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
        return FromStatus(status, interfaces, modemTelemetry, []);
    }

    public static XBondStatsSnapshot FromStatus(
        XBondStatus status,
        IReadOnlyList<InterfaceMetadataService.InterfaceMetadata> interfaces,
        IReadOnlyDictionary<string, F50ModemTelemetry> modemTelemetry,
        IReadOnlyList<InterfaceMetadataService.GatewayRoute> gatewayRoutes)
    {
        return FromStatus(status, interfaces, modemTelemetry, gatewayRoutes, EmptyNames, EmptyNames);
    }

    public static XBondStatsSnapshot FromStatus(
        XBondStatus status,
        IReadOnlyList<InterfaceMetadataService.InterfaceMetadata> interfaces,
        IReadOnlyDictionary<string, F50ModemTelemetry> modemTelemetry,
        IReadOnlyList<InterfaceMetadataService.GatewayRoute> gatewayRoutes,
        IReadOnlyDictionary<string, string> adapterAliases,
        IReadOnlyDictionary<string, string> ispNames)
    {
        var interfaceDisplayNames = interfaces
            .Where(item => !string.IsNullOrWhiteSpace(item.DisplayName))
            .ToDictionary(item => item.Device, item => item.DisplayName, StringComparer.OrdinalIgnoreCase);

        return FromStatus(
            status,
            interfaceDisplayNames,
            interfaces,
            modemTelemetry,
            gatewayRoutes,
            adapterAliases,
            ispNames);
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
            new Dictionary<string, F50ModemTelemetry>(StringComparer.OrdinalIgnoreCase),
            [],
            EmptyNames,
            EmptyNames);
    }

    private static XBondStatsSnapshot FromStatus(
        XBondStatus status,
        IReadOnlyDictionary<string, string> interfaceDisplayNames,
        IReadOnlyList<InterfaceMetadataService.InterfaceMetadata> interfaces,
        IReadOnlyDictionary<string, F50ModemTelemetry> modemTelemetry,
        IReadOnlyList<InterfaceMetadataService.GatewayRoute> gatewayRoutes,
        IReadOnlyDictionary<string, string> adapterAliases,
        IReadOnlyDictionary<string, string> ispNames)
    {
        var activeIds = status.Schedule.DataPathIds
            .Concat(status.Schedule.DuplicatePathIds)
            .Concat(status.Schedule.FecPathIds)
            .ToHashSet();
        var gatewayByDevice = gatewayRoutes
            .Where(item => !string.IsNullOrWhiteSpace(item.Device) && !string.IsNullOrWhiteSpace(item.Gateway))
            .GroupBy(item => item.Device, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Gateway, StringComparer.OrdinalIgnoreCase);

        var configuredPaths = status.Paths
            .Select(path =>
            {
                var lossPercent = path.LossRate * 100;
                var isStaleRtt = IsStaleRtt(path, lossPercent);
                var interfaceName = path.InterfaceName ?? path.BindDevice ?? $"path-{path.PathId}";
                gatewayByDevice.TryGetValue(interfaceName, out var gateway);
                modemTelemetry.TryGetValue(interfaceName, out var cellular);
                return new XBondPathStatsSnapshot
                {
                    PathId = path.PathId,
                    InterfaceName = interfaceName,
                    Name = AdapterNameResolver.Resolve(
                        interfaceName,
                        adapterAliases,
                        ispNames,
                        interfaceDisplayNames,
                        path.Name),
                    Role = path.Role,
                    InterfaceUp = path.InterfaceUp,
                    InCooldown = path.InCooldown,
                    DemotionReason = path.DemotionReason,
                    RoleReason = path.RoleReason,
                    SendFailureStreak = path.SendFailureStreak,
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
                    HeartbeatFailed = path.HeartbeatFailed,
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
                    Gateway = gateway,
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
                Name = AdapterNameResolver.Resolve(
                    item.Device,
                    adapterAliases,
                    ispNames,
                    interfaceDisplayNames,
                    item.DisplayName),
                Role = "standby",
                InterfaceUp = true,
                Gateway = gatewayByDevice.TryGetValue(item.Device, out var gateway) ? gateway : null,
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

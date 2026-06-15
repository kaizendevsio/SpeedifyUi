namespace XNetwork.Models;

public sealed class XBondStatsSnapshot
{
    public XBondStatus RawStatus { get; init; } = new();

    public bool IsRunning => RawStatus.Running;

    public bool HasError => RawStatus.HasError;

    public string? Error => RawStatus.Error;

    public string State => RawStatus.Tunnel.State;

    public string Message => RawStatus.Message;

    public string ServerAddress => RawStatus.ServerAddress;

    public string TunnelDevice => RawStatus.Tunnel.DeviceName ?? "xbond0";

    public string Mode => RawStatus.Mode;

    public DateTime UpdatedAtUtc => RawStatus.UpdatedAtUtc;

    public int? AnchorPathId => RawStatus.AnchorPathId;

    public IReadOnlyList<XBondPathStatsSnapshot> Paths { get; init; } = [];

    public IReadOnlyList<XBondPathStatsSnapshot> DashboardPaths =>
        Paths.Where(path => path.ShowOnDashboard).ToArray();

    public IReadOnlyList<XBondPathStatsSnapshot> ActivePaths =>
        DashboardPaths.Where(path => path.IsActive).ToArray();

    public IReadOnlyList<XBondPathStatsSnapshot> StandbyPaths =>
        DashboardPaths.Where(path => !path.IsActive).ToArray();

    public double DownloadMbps => RawStatus.InboundThroughputBps / 1_000_000d;

    public double UploadMbps => RawStatus.OutboundThroughputBps / 1_000_000d;

    public double AggregateThroughputMbps => DownloadMbps + UploadMbps;

    public double AverageRttMs => Paths
        .Where(path => path.IsActive && path.RttMs.HasValue)
        .Select(path => path.RttMs!.Value)
        .DefaultIfEmpty(0)
        .Average();

    public double MaxLossPercent => Paths
        .Where(path => path.IsActive)
        .Select(path => path.LossPercent)
        .DefaultIfEmpty(0)
        .Max();

    public ulong DataPacketsSent => RawStatus.DataPacketsSent;

    public ulong DuplicatePacketsSent => RawStatus.DuplicatePacketsSent;

    public ulong FecPacketsSent => RawStatus.FecPacketsSent;

    public ulong DataPacketsReceived => RawStatus.DataPacketsReceived;

    public ulong DataBytesSent => RawStatus.DataBytesSent;

    public ulong DataBytesReceived => RawStatus.DataBytesReceived;

    public bool Ipv6Supported => false;
}

public sealed class XBondPathStatsSnapshot
{
    public int PathId { get; init; }

    public string Name { get; init; } = "";

    public string InterfaceName { get; init; } = "";

    public string Role { get; init; } = "probe";

    public bool InterfaceUp { get; init; }

    public bool InCooldown { get; init; }

    public double Score { get; init; }

    public double? RttMs { get; init; }

    public double? JitterMs { get; init; }

    public double LossPercent { get; init; }

    public double LatePercent { get; init; }

    public int QueueDepth { get; init; }

    public ulong ThroughputBps { get; init; }

    public ulong OutboundThroughputBps { get; init; }

    public ulong InboundThroughputBps { get; init; }

    public ulong DuplicateInboundThroughputBps { get; init; }

    public ulong RawInboundThroughputBps { get; init; }

    public ulong TotalThroughputBps => ThroughputBps > 0
        ? ThroughputBps
        : OutboundThroughputBps + RawPathInboundThroughputBps;

    public double ThroughputMbps => TotalThroughputBps / 1_000_000d;

    public ulong RawPathInboundThroughputBps => RawInboundThroughputBps > 0
        ? RawInboundThroughputBps
        : InboundThroughputBps + DuplicateInboundThroughputBps;

    public double DownloadMbps => RawPathInboundThroughputBps / 1_000_000d;

    public double UsefulDownloadMbps => InboundThroughputBps / 1_000_000d;

    public double DuplicateDownloadMbps => DuplicateInboundThroughputBps / 1_000_000d;

    public double UploadMbps => OutboundThroughputBps / 1_000_000d;

    public string BindAddress { get; init; } = "";

    public string BindDevice { get; init; } = "";

    public bool IsActive { get; init; }

    public bool IsConfigured { get; init; } = true;

    public bool ShowOnDashboard => InterfaceUp || IsActive || InCooldown || !IsConfigured;

    public bool IsAnchor => string.Equals(Role, "anchor", StringComparison.OrdinalIgnoreCase);

    public string StateText
    {
        get
        {
            if (!IsConfigured && InterfaceUp)
            {
                return "Connected, not in XBond";
            }

            if (!InterfaceUp)
            {
                return "No carrier";
            }

            if (InCooldown)
            {
                return "Cooldown";
            }

            return IsActive ? "Active" : "Standby";
        }
    }
}

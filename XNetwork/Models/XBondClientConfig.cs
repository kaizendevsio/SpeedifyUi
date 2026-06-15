namespace XNetwork.Models;

public sealed class XBondClientConfig
{
    public bool Enabled { get; set; } = true;

    public ulong SessionId { get; set; } = 1;

    public string ServerAddress { get; set; } = "45.77.241.247:8444";

    public string Mode { get; set; } = "anchor-duplicate-1";

    public string RedundancyPolicy { get; set; } = "balanced";

    public int MaxActiveBackups { get; set; } = 1;

    public int RealtimeDeadlineMs { get; set; } = 500;

    public int InteractivePacketThresholdBytes { get; set; } = 768;

    public double DuplicateLossThreshold { get; set; } = 0.02;

    public double BackupLossDisableThreshold { get; set; } = 0.35;

    public int ReorderHoldMs { get; set; } = 25;

    public string RuntimeStatusPath { get; set; } = "/run/xbond/client-status.json";

    public List<XBondClientPathConfig> Paths { get; set; } = new();
}

public sealed class XBondClientPathConfig
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    public string InterfaceName { get; set; } = "";

    public string? BindAddress { get; set; }

    public bool Enabled { get; set; } = true;
}

public sealed class XBondAdapterConfigStatus
{
    public string Message { get; set; } = "";

    public string? Error { get; set; }

    public bool CanEdit { get; set; }

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public string Mode { get; set; } = "anchor-duplicate-1";

    public string RedundancyPolicy { get; set; } = "balanced";

    public int MaxActiveBackups { get; set; } = 1;

    public int RealtimeDeadlineMs { get; set; } = 500;

    public int InteractivePacketThresholdBytes { get; set; } = 768;

    public double DuplicateLossThreshold { get; set; } = 0.02;

    public double BackupLossDisableThreshold { get; set; } = 0.35;

    public int ReorderHoldMs { get; set; } = 25;

    public List<XBondAdapterConfigRow> Adapters { get; set; } = new();

    public bool HasError => !string.IsNullOrWhiteSpace(Error);
}

public sealed class XBondAdapterConfigRow
{
    public int? PathId { get; set; }

    public string Name { get; set; } = "";

    public string InterfaceName { get; set; } = "";

    public string Type { get; set; } = "";

    public string State { get; set; } = "";

    public bool IsConfigured { get; set; }

    public bool IsConnected { get; set; }

    public bool IsEligible { get; set; }

    public string Detail { get; set; } = "";

    public bool CanEnable => !IsConfigured && IsEligible;

    public bool CanDisable => IsConfigured;

    public bool CanToggle => CanEnable || CanDisable;
}

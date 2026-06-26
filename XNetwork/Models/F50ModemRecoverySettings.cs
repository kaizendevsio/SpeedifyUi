namespace XNetwork.Models;

public sealed class F50ModemRecoverySettings
{
    public bool Enabled { get; set; } = true;

    public int CheckIntervalMinutes { get; set; } = 3;

    public int FailedRecoveryCooldownRounds { get; set; } = 1;

    public int SettleTimeoutSeconds { get; set; } = 75;

    public bool UsbResetEnabled { get; set; } = true;

    public string UsbResetCommandPath { get; set; } = "usbreset";

    public string PingTarget { get; set; } = "45.77.241.247";

    public int PingCount { get; set; } = 2;

    public int PingTimeoutSeconds { get; set; } = 2;

    public int SevereLossPercent { get; set; } = 95;
}

public sealed class F50ModemRecoveryStatus
{
    public bool Enabled { get; set; }

    public bool IsRunning { get; set; }

    public int CheckIntervalMinutes { get; set; }

    public DateTimeOffset? LastStartedAtUtc { get; set; }

    public DateTimeOffset? LastCompletedAtUtc { get; set; }

    public DateTimeOffset? NextRunAtUtc { get; set; }

    public string Message { get; set; } = "F50 recovery has not run yet.";

    public List<F50ModemRecoveryEntryStatus> Modems { get; set; } = new();
}

public sealed class F50ModemRecoveryEntryStatus
{
    public string ProxyId { get; set; } = "";

    public string DisplayName { get; set; } = "";

    public string TargetHost { get; set; } = "";

    public string? InterfaceName { get; set; }

    public int? PathId { get; set; }

    public string Role { get; set; } = "";

    public double? RttMs { get; set; }

    public int? LossPercent { get; set; }

    public string? WanIpAddress { get; set; }

    public string? ModemState { get; set; }

    public string? PppStatus { get; set; }

    public string State { get; set; } = "unknown";

    public string LastAction { get; set; } = "none";

    public string LastResult { get; set; } = "";

    public string? LastError { get; set; }

    public int CooldownRoundsRemaining { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

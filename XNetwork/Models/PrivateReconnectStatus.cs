namespace XNetwork.Models;

public class PrivateReconnectStatus
{
    public bool IsEnabled { get; set; }

    public bool IsRunning { get; set; }

    public int IntervalMinutes { get; set; } = 30;

    public bool HealthTriggerEnabled { get; set; }

    public double HealthLatencyThresholdMs { get; set; } = 300;

    public int HealthDegradedSeconds { get; set; } = 120;

    public int HealthRecoveryObserveSeconds { get; set; } = 60;

    public int HealthCooldownMinutes { get; set; } = 15;

    public DateTime? LastAttemptUtc { get; set; }

    public DateTime? LastSuccessUtc { get; set; }

    public DateTime? LastHealthTriggerUtc { get; set; }

    public DateTime? LastHealthObservationUtc { get; set; }

    public DateTime? NextAttemptUtc { get; set; }

    public DateTime? HealthDegradedSinceUtc { get; set; }

    public DateTime? HealthSuppressedUntilUtc { get; set; }

    public double? LastHealthLatencyMs { get; set; }

    public string? LastState { get; set; }

    public string? LastError { get; set; }

    public string Message { get; set; } = "Private reconnect is disabled";

    public string? HealthMessage { get; set; }

    public List<PrivateReconnectEvent> RecentEvents { get; set; } = new();
}

public class PrivateReconnectEvent
{
    public DateTime TimestampUtc { get; set; }

    public string Message { get; set; } = "";

    public bool IsError { get; set; }
}

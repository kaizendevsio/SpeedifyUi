namespace XNetwork.Models;

public class PrivateReconnectSettings
{
    public bool Enabled { get; set; }

    public int IntervalMinutes { get; set; } = 30;

    public int DelaySeconds { get; set; } = 2;

    public bool HealthTriggerEnabled { get; set; }

    public double HealthLatencyThresholdMs { get; set; } = 300;

    public int HealthDegradedSeconds { get; set; } = 120;

    public int HealthRecoveryObserveSeconds { get; set; } = 60;

    public int HealthCooldownMinutes { get; set; } = 15;
}

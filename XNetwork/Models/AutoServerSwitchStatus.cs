namespace XNetwork.Models;

public class AutoServerSwitchStatus
{
    public bool IsEnabled { get; set; }

    public bool IsSwitching { get; set; }

    public bool IsCheckingProbe { get; set; }

    public string? RecommendedServer { get; set; }

    public string Message { get; set; } = "Auto server switching is disabled";

    public string? CurrentServer { get; set; }

    public string? LastSwitchReason { get; set; }

    public string? LastSwitchServer { get; set; }

    public DateTime? LastSwitchUtc { get; set; }

    public DateTime? DegradedSinceUtc { get; set; }

    public int HealthWindowSeconds { get; set; }

    public int HealthWindowSampleCount { get; set; }

    public int HealthWindowBadSampleCount { get; set; }

    public double HealthWindowBadSampleRatio { get; set; }

    public bool IsHealthWindowTriggered { get; set; }

    public DateTime? FirstBadSampleUtc { get; set; }

    public string? HealthWindowMessage { get; set; }

    public DateTime? LastProbeCheckUtc { get; set; }

    public DateTime? LastProbeSuccessUtc { get; set; }

    public double LastLatencyMs { get; set; }

    public double LastJitterMs { get; set; }

    public double LastSuccessRate { get; set; }

    public DateTime? LastProbeRunUtc { get; set; }

    public double? CurrentServerProbeScore { get; set; }

    public DateTime? CurrentServerProbeTestedUtc { get; set; }

    public bool IsCurrentServerProbeConfirmedBad { get; set; }

    public string? CurrentServerProbeMessage { get; set; }

    public double? RecommendedServerProbeScore { get; set; }

    public string? ProbeApiError { get; set; }

    public string? ProbeApiBaseUrl { get; set; }

    public int ProbeCheckCount { get; set; }

    public int FailedProbeCheckCount { get; set; }

    public int LastProbeScoreCount { get; set; }

    public int RecommendationCount { get; set; }

    public DateTime? LastRecommendationUtc { get; set; }

    public int SwitchAttemptCount { get; set; }

    public int SuccessfulSwitchCount { get; set; }

    public int FailedSwitchCount { get; set; }

    public int ConnectedAdapterCount { get; set; }

    public int MinimumConnectedAdaptersBeforeSwitch { get; set; }

    public bool IsLocalWanStableForSwitching { get; set; }

    public string? LocalWanStabilityMessage { get; set; }

    public string? PendingRecommendationServer { get; set; }

    public int RecommendationConfirmationCount { get; set; }

    public int RequiredRecommendationConfirmations { get; set; }

    public List<Adapter> Adapters { get; set; } = new();

    public List<ServerHealthScore> RecentScores { get; set; } = new();

    public List<AutoServerSwitchEvent> RecentEvents { get; set; } = new();
}

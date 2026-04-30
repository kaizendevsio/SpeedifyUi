namespace XNetwork.Models;

public class AutoServerSwitchPersistentState
{
    public DateTime? LastSwitchUtc { get; set; }

    public string? LastSwitchServer { get; set; }

    public string? LastSwitchReason { get; set; }

    public int ProbeCheckCount { get; set; }

    public int FailedProbeCheckCount { get; set; }

    public int RecommendationCount { get; set; }

    public DateTime? LastRecommendationUtc { get; set; }

    public int SwitchAttemptCount { get; set; }

    public int SuccessfulSwitchCount { get; set; }

    public int FailedSwitchCount { get; set; }

    public Dictionary<string, DateTime> AvoidServersUntil { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<AutoServerSwitchEvent> RecentEvents { get; set; } = new();
}

namespace XNetwork.Models;

public class AutoServerSwitchSettings
{
    public bool Enabled { get; set; }

    public string PreferredCountry { get; set; } = "sg";

    public string PreferredCity { get; set; } = "singapore";

    public List<string> FallbackCountries { get; set; } = new() { "hk", "jp", "ph" };

    public bool IncludePremiumServers { get; set; }

    public bool IncludePrivateServers { get; set; }

    public int CheckIntervalSeconds { get; set; } = 15;

    public int DegradedSecondsBeforeSwitch { get; set; } = 90;

    public int HealthWindowSeconds { get; set; } = 120;

    public int MinimumBadSamplesInWindow { get; set; } = 3;

    public double MinimumBadSampleRatio { get; set; } = 0.35;

    public bool RequireProbeCurrentServerBad { get; set; } = true;

    public int CooldownMinutes { get; set; } = 10;

    public int AvoidServerMinutes { get; set; } = 30;

    public int MinimumConnectedAdaptersBeforeSwitch { get; set; } = 2;

    public int RequiredRecommendationConfirmations { get; set; } = 2;

    public string ProbeApiBaseUrl { get; set; } = "";

    public string ProbeApiKey { get; set; } = "";

    public int ProbeRequestTimeoutSeconds { get; set; } = 10;

    public int ProbeMaxScoreAgeMinutes { get; set; } = 30;

    public double MinimumProbeScore { get; set; } = 70;

    public double MinimumScoreImprovement { get; set; } = 10;

    public double MaxLatencyMs { get; set; } = 250;

    public double MaxJitterMs { get; set; } = 80;

    public double MinSuccessRate { get; set; } = 85;
}

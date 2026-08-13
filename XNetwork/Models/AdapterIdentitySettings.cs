namespace XNetwork.Models;

/// <summary>
/// Configuration for third-party ISP identity lookups performed per network adapter.
/// </summary>
public sealed class AdapterIdentitySettings
{
    /// <summary>When false, no lookups are performed and cached identities are dropped.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Lookup endpoints, tried in order until one returns a parsable answer.</summary>
    public List<string> Endpoints { get; set; } =
    [
        "https://ipwho.is/",
        "http://ip-api.com/json/"
    ];

    public int RefreshIntervalSeconds { get; set; } = 60;

    public int SuccessTtlMinutes { get; set; } = 15;

    public int FailureBackoffSeconds { get; set; } = 120;

    public int RequestTimeoutSeconds { get; set; } = 5;

    public TimeSpan RefreshInterval => TimeSpan.FromSeconds(Math.Clamp(RefreshIntervalSeconds, 10, 3600));

    public TimeSpan SuccessTtl => TimeSpan.FromMinutes(Math.Clamp(SuccessTtlMinutes, 1, 1440));

    public TimeSpan FailureBackoff => TimeSpan.FromSeconds(Math.Clamp(FailureBackoffSeconds, 15, 3600));

    public TimeSpan RequestTimeout => TimeSpan.FromSeconds(Math.Clamp(RequestTimeoutSeconds, 1, 30));
}

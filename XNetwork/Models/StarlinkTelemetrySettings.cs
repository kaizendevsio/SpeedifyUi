namespace XNetwork.Models;

public sealed class StarlinkTelemetrySettings
{
    public bool Enabled { get; set; } = true;

    public string Host { get; set; } = "192.168.100.1";

    public int GrpcPort { get; set; } = 9200;

    public int GrpcWebPort { get; set; } = 9201;

    public bool UseGrpcWebFallback { get; set; } = true;

    public int PollIntervalSeconds { get; set; } = 5;

    public int RequestTimeoutSeconds { get; set; } = 2;

    public int StaleAfterSeconds { get; set; } = 30;

    public int HistoryMinutes { get; set; } = 5;

    public int HistoryMaxSamples { get; set; } = 120;

    public int CapabilityProbeIntervalSeconds { get; set; } = 3600;

    public List<string> AdapterNameHints { get; set; } = new() { "Starlink" };

    public bool AdapterProbeEnabled { get; set; } = true;

    public int AdapterProbeIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// Consecutive failed dish probes tolerated before a uLink path match is abandoned, when no
    /// other adapter answers either. An obstructed dish drops its management endpoint for a few
    /// seconds at a time, and treating the first miss as "the interface is gone" made the LAN
    /// access rules tear down and reapply on every reconcile.
    /// </summary>
    public int AdapterVerifyFailureTolerance { get; set; } = 3;

    public TimeSpan AdapterProbeInterval => TimeSpan.FromSeconds(Math.Clamp(AdapterProbeIntervalSeconds, 5, 300));

    public int VerifyFailureTolerance => Math.Clamp(AdapterVerifyFailureTolerance, 0, 20);

    public TimeSpan PollInterval => TimeSpan.FromSeconds(Math.Clamp(PollIntervalSeconds, 2, 300));

    public TimeSpan RequestTimeout => TimeSpan.FromSeconds(Math.Clamp(RequestTimeoutSeconds, 1, 30));

    public TimeSpan StaleAfter => TimeSpan.FromSeconds(Math.Clamp(StaleAfterSeconds, 5, 600));

    public TimeSpan HistoryAge => TimeSpan.FromMinutes(Math.Clamp(HistoryMinutes, 1, 60));

    public int HistorySampleLimit => Math.Clamp(HistoryMaxSamples, 10, 1000);

    public TimeSpan CapabilityProbeInterval => TimeSpan.FromSeconds(Math.Clamp(CapabilityProbeIntervalSeconds, 60, 86_400));
}

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

    public List<string> AdapterIdHints { get; set; } = new();

    public List<string> AdapterNameHints { get; set; } = new() { "Starlink" };

    public TimeSpan PollInterval => TimeSpan.FromSeconds(Math.Clamp(PollIntervalSeconds, 2, 300));

    public TimeSpan RequestTimeout => TimeSpan.FromSeconds(Math.Clamp(RequestTimeoutSeconds, 1, 30));

    public TimeSpan StaleAfter => TimeSpan.FromSeconds(Math.Clamp(StaleAfterSeconds, 5, 600));
}

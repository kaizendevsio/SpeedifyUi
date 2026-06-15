namespace XNetwork.Models;

public class XBondSpeedTestResult
{
    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? CompletedAtUtc { get; set; }

    public double? DownloadMbps { get; set; }

    public double? UploadMbps { get; set; }

    public double? PingMs { get; set; }

    public double? ServerDownloadMbps { get; set; }

    public double? ServerUploadMbps { get; set; }

    public string XBondServerHost { get; set; } = "";

    public int XBondServerPort { get; set; }

    public string XBondServerEndpoint => XBondServerPort > 0
        ? $"{XBondServerHost}:{XBondServerPort}"
        : XBondServerHost;

    public string? ServerTestError { get; set; }

    public double? PublicDownloadMbps
    {
        get => DownloadMbps;
        set => DownloadMbps = value;
    }

    public double? PublicUploadMbps
    {
        get => UploadMbps;
        set => UploadMbps = value;
    }

    public double? PublicPingMs
    {
        get => PingMs;
        set => PingMs = value;
    }

    public string PublicServerName
    {
        get => ServerName;
        set => ServerName = value;
    }

    public string PublicServerLocation
    {
        get => ServerLocation;
        set => ServerLocation = value;
    }

    public string? PublicTestError { get; set; }

    public bool IsSimulation { get; set; }

    public string? SimulatedInterface { get; set; }

    public string? SimulationProfile { get; set; }

    public bool SimulationCleanupSucceeded { get; set; }

    public string ServerName { get; set; } = "";

    public string ServerLocation { get; set; } = "";

    public string ClientIsp { get; set; } = "";

    public string ClientIp { get; set; } = "";

    public bool RouteUsesXBond { get; set; }

    public string RouteOutput { get; set; } = "";

    public string Message { get; set; } = "";

    public string? Error { get; set; }

    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    public bool ServerTestSucceeded => ServerDownloadMbps.HasValue && ServerUploadMbps.HasValue;

    public bool PublicTestSucceeded => PublicDownloadMbps.HasValue && PublicUploadMbps.HasValue;

    public bool Succeeded => !HasError && ServerTestSucceeded && PublicTestSucceeded;
}

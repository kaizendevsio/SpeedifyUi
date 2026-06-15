namespace XNetwork.Models;

public class XBondSpeedTestResult
{
    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? CompletedAtUtc { get; set; }

    public double? DownloadMbps { get; set; }

    public double? UploadMbps { get; set; }

    public double? PingMs { get; set; }

    public string ServerName { get; set; } = "";

    public string ServerLocation { get; set; } = "";

    public string ClientIsp { get; set; } = "";

    public string ClientIp { get; set; } = "";

    public bool RouteUsesXBond { get; set; }

    public string RouteOutput { get; set; } = "";

    public string Message { get; set; } = "";

    public string? Error { get; set; }

    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    public bool Succeeded => !HasError && DownloadMbps.HasValue && UploadMbps.HasValue;
}

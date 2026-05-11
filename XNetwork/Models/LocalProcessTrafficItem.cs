namespace XNetwork.Models;

public class LocalProcessTrafficItem
{
    public string ProcessName { get; init; } = "Unknown";

    public string Command { get; init; } = "";

    public int? ProcessId { get; init; }

    public double DownloadMbps { get; init; }

    public double UploadMbps { get; init; }

    public double TotalMbps => DownloadMbps + UploadMbps;
}

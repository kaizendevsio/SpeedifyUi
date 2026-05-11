namespace XNetwork.Models;

public class LocalProcessTrafficSnapshot
{
    public bool IsSupported { get; init; }

    public string Source { get; init; } = "";

    public string Message { get; init; } = "";

    public IReadOnlyList<LocalProcessTrafficItem> Processes { get; init; } = [];

    public double DownloadMbps => Processes.Sum(process => process.DownloadMbps);

    public double UploadMbps => Processes.Sum(process => process.UploadMbps);

    public static LocalProcessTrafficSnapshot Unsupported(string message)
    {
        return new LocalProcessTrafficSnapshot
        {
            IsSupported = false,
            Source = "Unavailable",
            Message = message,
            Processes = []
        };
    }
}

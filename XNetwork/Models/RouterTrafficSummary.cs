namespace XNetwork.Models;

public class RouterTrafficSummary
{
    public double SpeedifyDownloadMbps { get; init; }

    public double SpeedifyUploadMbps { get; init; }

    public double CudyClientDownloadMbps { get; init; }

    public double CudyClientUploadMbps { get; init; }

    public int CudyClientCount { get; init; }

    public double OtherDownloadMbps => Math.Max(0, SpeedifyDownloadMbps - CudyClientDownloadMbps);

    public double OtherUploadMbps => Math.Max(0, SpeedifyUploadMbps - CudyClientUploadMbps);

    public double OtherTotalMbps => OtherDownloadMbps + OtherUploadMbps;
}

namespace XNetwork.Models;

public class RouterTrafficSummary
{
    public double XBondDownloadMbps { get; init; }

    public double XBondUploadMbps { get; init; }

    public double CudyClientDownloadMbps { get; init; }

    public double CudyClientUploadMbps { get; init; }

    public int CudyClientCount { get; init; }

    public LocalProcessTrafficSnapshot LocalProcessTraffic { get; init; } = LocalProcessTrafficSnapshot.Unsupported("Local process attribution has not been sampled yet.");

    public double OtherDownloadMbps => Math.Max(0, XBondDownloadMbps - CudyClientDownloadMbps);

    public double OtherUploadMbps => Math.Max(0, XBondUploadMbps - CudyClientUploadMbps);

    public double OtherTotalMbps => OtherDownloadMbps + OtherUploadMbps;

    public double LocalProcessDownloadMbps => LocalProcessTraffic.DownloadMbps;

    public double LocalProcessUploadMbps => LocalProcessTraffic.UploadMbps;

    public double EstimatedOverheadDownloadMbps => Math.Max(0, OtherDownloadMbps - LocalProcessDownloadMbps);

    public double EstimatedOverheadUploadMbps => Math.Max(0, OtherUploadMbps - LocalProcessUploadMbps);

    public double EstimatedOverheadTotalMbps => EstimatedOverheadDownloadMbps + EstimatedOverheadUploadMbps;
}

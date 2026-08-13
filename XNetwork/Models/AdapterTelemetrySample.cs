namespace XNetwork.Models;

/// <summary>One timestamped per-adapter metric sample used by the adapter details charts.</summary>
public sealed class AdapterTelemetrySample
{
    public DateTimeOffset TimestampUtc { get; init; }

    public double? RttMs { get; init; }

    public double? LossPercent { get; init; }

    public double? JitterMs { get; init; }

    public double DownloadMbps { get; init; }

    public double UploadMbps { get; init; }
}

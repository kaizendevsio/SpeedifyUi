namespace XNetwork.Models;

public class CudyApControlStatus
{
    public bool IsEnabled { get; set; }

    public bool IsConfigured { get; set; }

    public bool IsBusy { get; set; }

    public bool? IsCudyApDisabled { get; set; }

    public string Message { get; set; } = "Cudy AP automation is disabled";

    public string? ManagementBaseUrl { get; set; }

    public string? DetectedHomeNetwork { get; set; }

    public string? DetectedHomeBssid { get; set; }

    public int? DetectedSignal { get; set; }

    public DateTime? LastHomeNetworkSeenUtc { get; set; }

    public DateTime? LastCheckUtc { get; set; }

    public DateTime? LastActionUtc { get; set; }

    public string? LastError { get; set; }

    public List<CudyApControlEvent> RecentEvents { get; set; } = new();
}

public class CudyApControlEvent
{
    public DateTime TimestampUtc { get; set; }

    public string Message { get; set; } = "";

    public bool IsError { get; set; }
}

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

public class XBondPerformanceMatrixResult
{
    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? CompletedAtUtc { get; set; }

    public string Message { get; set; } = "";

    public string? Error { get; set; }

    public string BottleneckSummary { get; set; } = "unknown";

    public string? ArtifactPath { get; set; }

    public XBondMatrixSystemSample Before { get; set; } = new();

    public XBondMatrixSystemSample After { get; set; } = new();

    public List<XBondPerformanceMatrixRun> Runs { get; set; } = new();

    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    public bool Succeeded => !HasError && Runs.Any(run => run.Succeeded);
}

public class XBondPerformanceMatrixRun
{
    public string Name { get; set; } = "";

    public string Kind { get; set; } = "";

    public string? InterfaceName { get; set; }

    public string? Mode { get; set; }

    public string? Policy { get; set; }

    public string? Anchor { get; set; }

    public string? Backup { get; set; }

    public string? ProbePaths { get; set; }

    public int? Mtu { get; set; }

    public bool MssClampEnabled { get; set; }

    public double? DownloadMbps { get; set; }

    public double? UploadMbps { get; set; }

    public double? RttMs { get; set; }

    public double? LossPercent { get; set; }

    public double? LatePercent { get; set; }

    public int? QueueDepth { get; set; }

    public ulong? PacketRatePps { get; set; }

    public ulong? Retransmits { get; set; }

    public double? ProcessCpuPercent { get; set; }

    public ulong? ProcessRssBytes { get; set; }

    public string? Bottleneck { get; set; }

    public string? Error { get; set; }

    public bool Succeeded => string.IsNullOrWhiteSpace(Error) && (DownloadMbps.HasValue || UploadMbps.HasValue);
}

public class XBondMatrixSystemSample
{
    public DateTime SampledAtUtc { get; set; } = DateTime.UtcNow;

    public ulong? DataPacketsSent { get; set; }

    public ulong? DataPacketsReceived { get; set; }

    public ulong? DuplicatePacketsSent { get; set; }

    public ulong? LatePacketsDropped { get; set; }

    public ulong? ReorderPendingDepth { get; set; }

    public ulong? ReorderHeldPackets { get; set; }

    public ulong? ReorderReleasedGapPackets { get; set; }

    public ulong? ReorderLateDuplicates { get; set; }

    public ulong? ProcessRssBytes { get; set; }
}

public class XBondMtuSweepResult
{
    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? CompletedAtUtc { get; set; }

    public string Message { get; set; } = "";

    public string? Error { get; set; }

    public int? CurrentMtu { get; set; }

    public int? RecommendedMtu { get; set; }

    public int? RecommendedMss { get; set; }

    public bool MssClampEnabled { get; set; }

    public List<XBondMtuSweepProbe> Probes { get; set; } = new();

    public bool HasError => !string.IsNullOrWhiteSpace(Error);
}

public class XBondMtuSweepProbe
{
    public int Mtu { get; set; }

    public int PayloadSize { get; set; }

    public bool Succeeded { get; set; }

    public string Message { get; set; } = "";
}

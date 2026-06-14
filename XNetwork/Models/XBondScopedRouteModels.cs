namespace XNetwork.Models;

public class XBondScopedRouteStatus
{
    public string Target { get; set; } = "";

    public string TargetCidr => string.IsNullOrWhiteSpace(Target) ? "" : $"{Target}/32";

    public string TunnelDevice { get; set; } = "";

    public string SourceAddress { get; set; } = "";

    public bool RouteUsesXBond { get; set; }

    public string RouteOutput { get; set; } = "";

    public string Message { get; set; } = "";

    public string? Error { get; set; }

    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class XBondScopedRouteTestResult : XBondScopedRouteStatus
{
    public int Sent { get; set; }

    public int Received { get; set; }

    public int Lost { get; set; }

    public double LossRate { get; set; }

    public double? MinRttMs { get; set; }

    public double? AvgRttMs { get; set; }

    public double? MaxRttMs { get; set; }

    public bool RouteRemoved { get; set; }

    public bool Succeeded => !HasError && Sent > 0 && Received == Sent;
}

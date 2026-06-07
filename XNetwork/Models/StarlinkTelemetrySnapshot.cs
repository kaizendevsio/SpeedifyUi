namespace XNetwork.Models;

public sealed record StarlinkTelemetrySnapshot
{
    public bool IsAvailable { get; init; }

    public DateTimeOffset? LastUpdatedUtc { get; init; }

    public string? Error { get; init; }

    public string? DeviceId { get; init; }

    public string? HardwareVersion { get; init; }

    public string? SoftwareVersion { get; init; }

    public string? DishState { get; init; }

    public long? UptimeSeconds { get; init; }

    public double? PopPingLatencyMs { get; init; }

    public double? PopPingDropRate { get; init; }

    public double? DownlinkMbps { get; init; }

    public double? UplinkMbps { get; init; }

    public double? ObstructionPercent { get; init; }

    public bool? CurrentlyObstructed { get; init; }

    public bool? GpsValid { get; init; }

    public int? GpsSatellites { get; init; }

    public double? BoresightAzimuthDegrees { get; init; }

    public double? BoresightElevationDegrees { get; init; }

    public double? AlignmentErrorDegrees { get; init; }

    public bool? HasActuators { get; init; }

    public IReadOnlyList<string> ActiveAlerts { get; init; } = Array.Empty<string>();

    public bool IsStale(TimeSpan staleAfter)
    {
        return LastUpdatedUtc is null || DateTimeOffset.UtcNow - LastUpdatedUtc.Value > staleAfter;
    }

    public static StarlinkTelemetrySnapshot Unavailable(string? error = null)
    {
        return new StarlinkTelemetrySnapshot
        {
            IsAvailable = false,
            Error = error
        };
    }
}

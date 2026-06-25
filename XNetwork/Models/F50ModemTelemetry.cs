using System.Text.Json.Serialization;

namespace XNetwork.Models;

public sealed class F50ModemTelemetry
{
    public string Host { get; init; } = "";

    public string? Generation { get; init; }

    public int? SignalBars { get; init; }

    public DateTime UpdatedAtUtc { get; init; } = DateTime.UtcNow;

    public bool IsAvailable => !string.IsNullOrWhiteSpace(Generation) || SignalBars.HasValue;
}

public sealed class F50ModemTelemetryResponse
{
    [JsonPropertyName("network_type")]
    public string? NetworkType { get; init; }

    [JsonPropertyName("current_network_type")]
    public string? CurrentNetworkType { get; init; }

    [JsonPropertyName("signalbar")]
    public string? SignalBar { get; init; }

    [JsonPropertyName("Error")]
    public string? Error { get; init; }
}


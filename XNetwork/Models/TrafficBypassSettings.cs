using System.Text.Json.Serialization;

namespace XNetwork.Models;

public sealed class TrafficBypassSettings
{
    public List<TrafficBypassRule> Rules { get; set; } = new();

    public string ApplyHelperPath { get; set; } = "/usr/local/sbin/xnetwork-traffic-bypass-apply";

    public int CommandTimeoutSeconds { get; set; } = 15;
}

public sealed class TrafficBypassRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string DisplayName { get; set; } = "";

    public bool Enabled { get; set; } = true;

    public List<string> Destinations { get; set; } = new();

    public string Protocol { get; set; } = TrafficBypassProtocols.Any;

    public List<string> Ports { get; set; } = new();

    public string EgressMode { get; set; } = TrafficBypassEgressModes.AutoPhysical;

    public string InterfaceName { get; set; } = "";
}

public sealed class TrafficBypassValidationResult
{
    public bool IsValid => Errors.Count == 0;

    public List<string> Errors { get; } = new();
}

public sealed record TrafficBypassApplyStatus(
    bool IsSupported,
    bool Applied,
    string Message,
    string? Error,
    DateTimeOffset UpdatedAtUtc);

public sealed record TrafficBypassEgressAdapter(
    string InterfaceName,
    string DisplayName,
    string State);

public static class TrafficBypassProtocols
{
    public const string Any = "any";
    public const string Tcp = "tcp";
    public const string Udp = "udp";

    public static string Normalize(string? value)
    {
        value = value?.Trim().ToLowerInvariant();
        return value is Tcp or Udp ? value : Any;
    }

    public static bool IsKnown(string? value) => Normalize(value) == value?.Trim().ToLowerInvariant();
}

public static class TrafficBypassEgressModes
{
    public const string AutoPhysical = "auto";
    public const string Interface = "interface";

    public static string Normalize(string? value)
    {
        value = value?.Trim().ToLowerInvariant();
        return value == Interface ? Interface : AutoPhysical;
    }
}

public sealed class TrafficBypassHelperPayload
{
    [JsonPropertyName("rules")]
    public List<TrafficBypassRule> Rules { get; set; } = new();
}

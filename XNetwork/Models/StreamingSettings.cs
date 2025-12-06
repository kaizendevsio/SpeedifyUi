using System.Text.Json.Serialization;

namespace XNetwork.Models;

/// <summary>
/// Settings for streaming mode high-priority traffic.
/// Defines traffic that should receive priority in streaming mode.
/// </summary>
public class StreamingSettings
{
    /// <summary>
    /// List of domains for high-priority streaming traffic.
    /// </summary>
    [JsonPropertyName("domains")]
    public List<string> Domains { get; set; } = new();

    /// <summary>
    /// List of IPv4 addresses for high-priority streaming traffic.
    /// </summary>
    [JsonPropertyName("ipv4")]
    public List<string> Ipv4 { get; set; } = new();

    /// <summary>
    /// List of IPv6 addresses for high-priority streaming traffic.
    /// </summary>
    [JsonPropertyName("ipv6")]
    public List<string> Ipv6 { get; set; } = new();

    /// <summary>
    /// List of port rules for high-priority streaming traffic.
    /// </summary>
    [JsonPropertyName("ports")]
    public List<PortRule> Ports { get; set; } = new();
}
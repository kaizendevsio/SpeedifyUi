using System.Text.Json.Serialization;

namespace XNetwork.Models;

/// <summary>
/// Fixed delay configuration for traffic shaping.
/// Applies consistent latency to specified traffic.
/// </summary>
public class FixedDelaySettings
{
    /// <summary>
    /// Delay in milliseconds to apply.
    /// </summary>
    [JsonPropertyName("delayMs")]
    public int DelayMs { get; set; }

    /// <summary>
    /// List of domains to apply fixed delay to.
    /// </summary>
    [JsonPropertyName("domains")]
    public List<string> Domains { get; set; } = new();

    /// <summary>
    /// List of IP addresses to apply fixed delay to.
    /// </summary>
    [JsonPropertyName("ips")]
    public List<string> Ips { get; set; } = new();

    /// <summary>
    /// List of port rules to apply fixed delay to.
    /// </summary>
    [JsonPropertyName("ports")]
    public List<PortRule> Ports { get; set; } = new();
}
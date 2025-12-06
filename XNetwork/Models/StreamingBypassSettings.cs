using System.Text.Json.Serialization;

namespace XNetwork.Models;

/// <summary>
/// Settings for streaming bypass configuration.
/// Defines traffic that should bypass the VPN tunnel.
/// </summary>
public class StreamingBypassSettings
{
    /// <summary>
    /// Whether domain watchlist bypass is enabled.
    /// </summary>
    [JsonPropertyName("domainWatchlistEnabled")]
    public bool DomainWatchlistEnabled { get; set; }

    /// <summary>
    /// List of domains to bypass.
    /// </summary>
    [JsonPropertyName("domains")]
    public List<string> Domains { get; set; } = new();

    /// <summary>
    /// List of IPv4 addresses to bypass.
    /// </summary>
    [JsonPropertyName("ipv4")]
    public List<string> Ipv4 { get; set; } = new();

    /// <summary>
    /// List of IPv6 addresses to bypass.
    /// </summary>
    [JsonPropertyName("ipv6")]
    public List<string> Ipv6 { get; set; } = new();

    /// <summary>
    /// List of port rules to bypass.
    /// </summary>
    [JsonPropertyName("ports")]
    public List<PortRule> Ports { get; set; } = new();

    /// <summary>
    /// List of service bypass configurations.
    /// </summary>
    [JsonPropertyName("services")]
    public List<ServiceBypass> Services { get; set; } = new();
}

/// <summary>
/// Represents a port or port range rule with protocol.
/// </summary>
public class PortRule
{
    /// <summary>
    /// The port number or start of port range.
    /// </summary>
    [JsonPropertyName("port")]
    public int Port { get; set; }

    /// <summary>
    /// Optional end of port range. If null, only single port is specified.
    /// </summary>
    [JsonPropertyName("portRangeEnd")]
    public int? PortRangeEnd { get; set; }

    /// <summary>
    /// Protocol type: "tcp" or "udp".
    /// </summary>
    [JsonPropertyName("protocol")]
    public string Protocol { get; set; } = "tcp";
}

/// <summary>
/// Represents a service bypass configuration.
/// </summary>
public class ServiceBypass
{
    /// <summary>
    /// Name of the service (e.g., "netflix", "youtube").
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>
    /// Whether bypass is enabled for this service.
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }
}
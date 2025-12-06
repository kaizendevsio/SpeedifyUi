using System.Text.Json.Serialization;

namespace XNetwork.Models;

/// <summary>
/// Enterprise subnet routing configuration.
/// Defines downstream networks to route through the VPN.
/// </summary>
public class DownstreamSubnet
{
    /// <summary>
    /// Network address (e.g., "192.168.1.0").
    /// </summary>
    [JsonPropertyName("address")]
    public string Address { get; set; } = "";

    /// <summary>
    /// CIDR prefix length (e.g., 24 for /24 subnet).
    /// </summary>
    [JsonPropertyName("prefixLength")]
    public int PrefixLength { get; set; }
}
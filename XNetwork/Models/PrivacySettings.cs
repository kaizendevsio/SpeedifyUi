using System.Text.Json.Serialization;

namespace XNetwork.Models;

/// <summary>
/// Privacy-related settings for the VPN connection.
/// </summary>
public class PrivacySettings
{
    /// <summary>
    /// List of custom DNS server addresses.
    /// </summary>
    [JsonPropertyName("dnsAddresses")]
    public List<string> DnsAddresses { get; set; } = new();

    /// <summary>
    /// Whether DNS leak protection is enabled.
    /// </summary>
    [JsonPropertyName("dnsLeak")]
    public bool DnsLeak { get; set; }

    /// <summary>
    /// Whether IP leak protection is enabled.
    /// </summary>
    [JsonPropertyName("ipLeak")]
    public bool IpLeak { get; set; }

    /// <summary>
    /// Whether kill switch is enabled (blocks internet if VPN disconnects).
    /// </summary>
    [JsonPropertyName("killSwitch")]
    public bool KillSwitch { get; set; }

    /// <summary>
    /// Whether to request browsers to disable DNS-over-HTTPS.
    /// </summary>
    [JsonPropertyName("requestToDisableDoH")]
    public bool RequestToDisableDoH { get; set; }
}
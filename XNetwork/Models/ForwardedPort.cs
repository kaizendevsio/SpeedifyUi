using System.Text.Json.Serialization;

namespace XNetwork.Models;

/// <summary>
/// Port forwarding configuration.
/// </summary>
public class ForwardedPort
{
    /// <summary>
    /// Port number to forward.
    /// </summary>
    [JsonPropertyName("port")]
    public int Port { get; set; }

    /// <summary>
    /// Protocol type: "tcp" or "udp".
    /// </summary>
    [JsonPropertyName("protocol")]
    public string Protocol { get; set; } = "tcp";
}
using System.Text.Json.Serialization;

namespace XNetwork.Models;

/// <summary>
/// Transport layer configuration settings.
/// </summary>
public class TransportSettings
{
    /// <summary>
    /// Transport mode: "auto", "tcp", "tcp-multi", "udp", or "https".
    /// </summary>
    [JsonPropertyName("transportMode")]
    public string TransportMode { get; set; } = "auto";

    /// <summary>
    /// Seconds to wait before retrying transport connection.
    /// </summary>
    [JsonPropertyName("transportRetrySeconds")]
    public int TransportRetrySeconds { get; set; } = 30;

    /// <summary>
    /// Seconds to wait before retrying server connection.
    /// </summary>
    [JsonPropertyName("connectRetrySeconds")]
    public int ConnectRetrySeconds { get; set; } = 30;
}
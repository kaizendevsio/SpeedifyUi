using System.Text.Json.Serialization;

namespace XNetwork.Models;

/// <summary>
/// Server connection method configuration.
/// </summary>
public class ConnectMethod
{
    /// <summary>
    /// Connection method: "closest", "public", "private", "p2p", or a country code.
    /// </summary>
    [JsonPropertyName("method")]
    public string Method { get; set; } = "closest";

    /// <summary>
    /// Country code for server selection (when method is a country code).
    /// </summary>
    [JsonPropertyName("country")]
    public string Country { get; set; } = "";

    /// <summary>
    /// City name for server selection.
    /// </summary>
    [JsonPropertyName("city")]
    public string City { get; set; } = "";

    /// <summary>
    /// Server number for specific server selection.
    /// </summary>
    [JsonPropertyName("num")]
    public int Num { get; set; }
}
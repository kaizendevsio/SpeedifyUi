using System.Text.Json.Serialization;

namespace XNetwork.Models;

public class ConnectionStatsPayload
{
    [JsonPropertyName("connections")]
    public List<ConnectionItem> Connections { get; set; } = new();

    [JsonPropertyName("time")]
    public long Time { get; set; }
}

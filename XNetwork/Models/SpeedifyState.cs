using System.Text.Json.Serialization;

namespace XNetwork.Models;

public class SpeedifyState
{
    [JsonPropertyName("state")]
    public string State { get; set; } = "";
}

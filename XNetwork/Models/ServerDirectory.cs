using System.Text.Json.Serialization;

namespace XNetwork.Models;

public class ServerDirectory
{
    [JsonPropertyName("public")]
    public List<ServerInfo> Public { get; set; } = new();

    [JsonPropertyName("private")]
    public List<ServerInfo> Private { get; set; } = new();
}

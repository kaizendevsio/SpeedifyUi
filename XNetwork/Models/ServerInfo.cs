using System.Text.Json.Serialization;

namespace XNetwork.Models;

public class ServerInfo
{
    [JsonPropertyName("city")]
    public string City { get; set; } = string.Empty;
    
    [JsonPropertyName("country")]
    public string Country { get; set; } = string.Empty;
    
    [JsonPropertyName("datacenter")]
    public string DataCenter { get; set; } = string.Empty;
    
    [JsonPropertyName("friendlyName")]
    public string FriendlyName { get; set; } = string.Empty;
    
    [JsonPropertyName("isPremium")]
    public bool IsPremium { get; set; }
    
    [JsonPropertyName("isPrivate")]
    public bool IsPrivate { get; set; }
    
    [JsonPropertyName("num")]
    public int Num { get; set; }
    
    [JsonPropertyName("publicIp")]
    public List<string> PublicIP { get; set; } = new();
    
    [JsonPropertyName("tag")]
    public string Tag { get; set; } = string.Empty;
    
    [JsonPropertyName("torrentAllowed")]
    public bool TorrentAllowed { get; set; }
}
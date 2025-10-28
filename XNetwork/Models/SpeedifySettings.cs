using System.Text.Json.Serialization;

namespace XNetwork.Models;

public class SpeedifySettings
{
    [JsonPropertyName("encrypted")]
    public bool Encrypted { get; set; }

    [JsonPropertyName("headerCompression")]
    public bool HeaderCompression { get; set; }

    [JsonPropertyName("packetAggregation")]
    public bool PacketAggregation { get; set; }

    [JsonPropertyName("jumboPackets")]
    public bool JumboPackets { get; set; }

    [JsonPropertyName("bondingMode")]
    public string BondingMode { get; set; } = "speed";

    [JsonPropertyName("enableDefaultRoute")]
    public bool EnableDefaultRoute { get; set; }

    [JsonPropertyName("allowChaChaEncryption")]
    public bool AllowChaChaEncryption { get; set; }

    [JsonPropertyName("enableAutomaticPriority")]
    public bool EnableAutomaticPriority { get; set; }

    [JsonPropertyName("overflowThreshold")]
    public double OverflowThreshold { get; set; }

    [JsonPropertyName("perConnectionEncryptionEnabled")]
    public bool PerConnectionEncryptionEnabled { get; set; }
}
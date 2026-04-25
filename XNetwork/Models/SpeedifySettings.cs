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

    [JsonPropertyName("priorityOverflowThreshold")]
    public double PriorityOverflowThreshold { get; set; }

    [JsonPropertyName("maxRedundant")]
    public int MaxRedundant { get; set; }

    [JsonPropertyName("startupConnect")]
    public bool StartupConnect { get; set; }

    [JsonPropertyName("targetNumberOfConnections")]
    public TargetNumberOfConnections TargetNumberOfConnections { get; set; } = new();

    [JsonPropertyName("fixedDelay")]
    public int FixedDelay { get; set; }

    [JsonPropertyName("forwardedPorts")]
    public List<ForwardedPort> ForwardedPorts { get; set; } = new();

    [JsonPropertyName("downstreamSubnets")]
    public List<DownstreamSubnet> DownstreamSubnets { get; set; } = new();

    [JsonPropertyName("perConnectionEncryptionEnabled")]
    public bool PerConnectionEncryptionEnabled { get; set; }
}

public class TargetNumberOfConnections
{
    [JsonPropertyName("upload")]
    public int Upload { get; set; }

    [JsonPropertyName("download")]
    public int Download { get; set; }
}

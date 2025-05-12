using System.Text.Json.Serialization;

namespace XNetwork.Models;

public class ConnectionItem
{
    [JsonPropertyName("AdapterId")]
    public string AdapterId { get; set; }

    [JsonPropertyName("connectionID")]
    public string ConnectionId { get; set; } // Useful for filtering

    [JsonPropertyName("receiveBps")]
    public double ReceiveBps { get; set; } // This is your download speed

    [JsonPropertyName("sendBps")]
    public double SendBps { get; set; }    // This is your upload speed

    [JsonPropertyName("latencyMs")]
    public double LatencyMs { get; set; }  // This is your RTT

    [JsonPropertyName("lossSend")]
    public double LossSend { get; set; }   // Send loss as a fraction (e.g., 0.01 for 1%)

    // You can add other fields from the JSON here if you need them later, e.g.:
    // [JsonPropertyName("lossReceive")]
    // public double LossReceive { get; set; }
    // [JsonPropertyName("jitterMs")]
    // public double JitterMs { get; set; }
    // [JsonPropertyName("mos")]
    // public double Mos { get; set; }
}
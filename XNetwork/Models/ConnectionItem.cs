using System.Text.Json.Serialization;

namespace XNetwork.Models;

using System.Text.Json.Serialization;

/// <summary>
///     Represents a single adapter/connection entry returned by the Speedify CLI “stats” output.
///     Every field is mapped 1-to-1 to the raw JSON so that <c>JsonSerializer.Deserialize&lt;ConnectionItem&gt;()</c>
///     works out-of-the-box.
/// </summary>
public class ConnectionItem
{
    // ───────────────────────────── Identification ─────────────────────────────

    /// <summary>
    ///     Logical adapter name (e.g. <c>eth0</c>, <c>wlan0</c>, <c>cell0</c>).
    /// </summary>
    [JsonPropertyName("adapterID")]
    public string AdapterId { get; set; }

    /// <summary>
    ///     Unique identifier for the live Speedify connection (<c>adapterID%protocol</c>).
    ///     Handy for filtering or correlating with other API calls.
    /// </summary>
    [JsonPropertyName("connectionID")]
    public string ConnectionId { get; set; }

    // ───────────────────────────── State Flags ─────────────────────────────

    /// <summary> <c>true</c> if the adapter is currently part of the Speedify bond. </summary>
    [JsonPropertyName("connected")]
    public bool Connected { get; set; }

    /// <summary> Aggregate congestion flag calculated by Speedify. </summary>
    [JsonPropertyName("congested")]
    public bool Congested { get; set; }

    /// <summary> Congestion affecting downstream traffic. </summary>
    [JsonPropertyName("downloadCongested")]
    public bool DownloadCongested { get; set; }

    /// <summary> Congestion affecting upstream traffic. </summary>
    [JsonPropertyName("uploadCongested")]
    public bool UploadCongested { get; set; }

    /// <summary> Indicates that the adapter is in “sleep” / low-power mode. </summary>
    [JsonPropertyName("sleeping")]
    public bool Sleeping { get; set; }

    // ───────────────────────────── Throughput ─────────────────────────────

    /// <summary> Current downstream throughput in <b>bytes / second</b>. </summary>
    [JsonPropertyName("receiveBps")]
    public double ReceiveBps { get; set; }

    /// <summary> Current upstream throughput in <b>bytes / second</b>. </summary>
    [JsonPropertyName("sendBps")]
    public double SendBps { get; set; }

    /// <summary> Combined send + receive throughput in <b>bytes / second</b>. </summary>
    [JsonPropertyName("totalBps")]
    public double TotalBps { get; set; }

    /// <summary> Cumulative bytes received since the connection was established. </summary>
    [JsonPropertyName("receiveBytes")]
    public long ReceiveBytes { get; set; }

    /// <summary> Cumulative bytes sent since the connection was established. </summary>
    [JsonPropertyName("sendBytes")]
    public long SendBytes { get; set; }

    /// <summary> Downstream speed estimate averaged by Speedify in <b>megabits / second</b>. </summary>
    [JsonPropertyName("receiveEstimateMbps")]
    public double ReceiveEstimateMbps { get; set; }

    /// <summary> Upstream speed estimate averaged by Speedify in <b>megabits / second</b>. </summary>
    [JsonPropertyName("sendEstimateMbps")]
    public double SendEstimateMbps { get; set; }

    // ───────────────────────────── Link Quality ─────────────────────────────

    /// <summary> Round-trip latency in milliseconds. </summary>
    [JsonPropertyName("latencyMs")]
    public double LatencyMs { get; set; }

    /// <summary> Jitter (latency variation) in milliseconds. </summary>
    [JsonPropertyName("jitterMs")]
    public double JitterMs { get; set; }

    /// <summary> Fraction of packets lost on the send path in percentage (0 – 1). </summary>
    [JsonPropertyName("lossSend")]
    public double LossSend { get; set; }

    /// <summary> Fraction of packets lost on the receive path percentage (0 – 1). </summary>
    [JsonPropertyName("lossReceive")]
    public double LossReceive { get; set; }

    /// <summary>
    ///     Mean Opinion Score (1 – 5) estimated by Speedify; higher is better.
    ///     Useful for VoIP / real-time media quality assessment.
    /// </summary>
    [JsonPropertyName("mos")]
    public double Mos { get; set; }

    // ───────────────────────────── Socket / Flow Control ─────────────────────────────

    /// <summary> Number of active TCP/UDP sockets multiplexed over the connection. </summary>
    [JsonPropertyName("numberOfSockets")]
    public int NumberOfSockets { get; set; }

    /// <summary> Bytes currently in flight (sent but not yet acknowledged). </summary>
    [JsonPropertyName("inFlight")]
    public long InFlight { get; set; }

    /// <summary> Maximum allowed in-flight bytes for congestion control. </summary>
    [JsonPropertyName("inFlightWindow")]
    public long InFlightWindow { get; set; }

    // ───────────────────────────── Addressing / Protocol ─────────────────────────────

    /// <summary> Private (LAN) IPv4/IPv6 address of the adapter. </summary>
    [JsonPropertyName("privateIp")]
    public string? PrivateIp { get; set; }

    /// <summary>
    ///     Local address used by Speedify for the tunnel (may be empty on some platforms).
    /// </summary>
    [JsonPropertyName("localIp")]
    public string? LocalIp { get; set; }

    /// <summary> Public / remote IP of the Speedify server endpoint. </summary>
    [JsonPropertyName("remoteIp")]
    public string? RemoteIp { get; set; }

    /// <summary> Transport protocol used by the connection (e.g. <c>udp</c>, <c>tcp</c>, <c>proxy</c>). </summary>
    [JsonPropertyName("protocol")]
    public string? Protocol { get; set; }
}

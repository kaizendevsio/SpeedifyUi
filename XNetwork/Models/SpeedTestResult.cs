using System.Text.Json.Serialization;

namespace XNetwork.Models;

public class SpeedTestResult
{
    [JsonPropertyName("city")]
    public string City { get; set; } = string.Empty;

    [JsonPropertyName("country")]
    public string Country { get; set; } = string.Empty;

    [JsonPropertyName("downloadSpeed")]
    public double DownloadSpeed { get; set; }

    [JsonPropertyName("uploadSpeed")]
    public double UploadSpeed { get; set; }

    [JsonPropertyName("latency")]
    public int Latency { get; set; }

    [JsonPropertyName("numConnections")]
    public int NumConnections { get; set; }

    [JsonPropertyName("time")]
    public long Time { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("isError")]
    public bool IsError { get; set; }

    // Stream test specific properties
    [JsonPropertyName("fps")]
    public int? Fps { get; set; }

    [JsonPropertyName("jitter")]
    public int? Jitter { get; set; }

    [JsonPropertyName("loss")]
    public double? Loss { get; set; }

    [JsonPropertyName("resolution")]
    public string? Resolution { get; set; }

    /// <summary>
    /// Gets the timestamp as a DateTime
    /// </summary>
    public DateTime Timestamp => DateTimeOffset.FromUnixTimeSeconds(Time).DateTime;

    /// <summary>
    /// Returns true if this is a stream test result
    /// </summary>
    public bool IsStreamTest => Type == "streaming";

    /// <summary>
    /// Returns true if this is a speed test result
    /// </summary>
    public bool IsSpeedTest => Type == "speed";
}
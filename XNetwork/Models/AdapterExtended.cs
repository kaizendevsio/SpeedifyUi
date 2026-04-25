using System.Text.Json;
using System.Text.Json.Serialization;

namespace XNetwork.Models;

/// <summary>
/// Extended adapter model with all configurable settings.
/// </summary>
public class AdapterExtended
{
    /// <summary>
    /// Unique identifier for the adapter.
    /// </summary>
    [JsonPropertyName("adapterID")]
    public string AdapterId { get; set; } = "";

    /// <summary>
    /// Display name of the adapter.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>
    /// Internet Service Provider name.
    /// </summary>
    [JsonPropertyName("isp")]
    public string Isp { get; set; } = "";

    /// <summary>
    /// Current connection state.
    /// </summary>
    [JsonPropertyName("state")]
    public string State { get; set; } = "";

    /// <summary>
    /// Adapter type (e.g., "Ethernet", "Wi-Fi", "Cellular").
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    /// <summary>
    /// Priority setting: "automatic", "always", "secondary", "backup", or "never".
    /// </summary>
    [JsonPropertyName("priority")]
    public string Priority { get; set; } = "automatic";

    /// <summary>
    /// Current working priority after automatic adjustments.
    /// </summary>
    [JsonPropertyName("workingPriority")]
    public string WorkingPriority { get; set; } = "";

    /// <summary>
    /// Whether encryption is enabled for this adapter.
    /// </summary>
    [JsonPropertyName("encryptionEnabled")]
    public bool EncryptionEnabled { get; set; } = true;

    /// <summary>
    /// Whether to expose DSCP markings on this adapter.
    /// </summary>
    [JsonPropertyName("exposeDscp")]
    public bool ExposeDscp { get; set; }

    /// <summary>
    /// Data usage tracking and limits.
    /// </summary>
    [JsonPropertyName("dataUsage")]
    public AdapterDataUsage DataUsage { get; set; } = new();

    /// <summary>
    /// Rate limiting settings.
    /// </summary>
    [JsonPropertyName("rateLimit")]
    [JsonConverter(typeof(AdapterRateLimitJsonConverter))]
    public AdapterRateLimit RateLimit { get; set; } = new();

    /// <summary>
    /// Directional upload/download settings.
    /// </summary>
    [JsonPropertyName("directionalSettings")]
    public AdapterDirectionalSettings DirectionalSettings { get; set; } = new();
}

/// <summary>
/// Data usage tracking and limits for an adapter.
/// </summary>
public class AdapterDataUsage
{
    /// <summary>
    /// Rate limit applied when over data limit (bytes/sec, 0 = blocked).
    /// </summary>
    [JsonPropertyName("overlimitRatelimit")]
    public long OverlimitRatelimit { get; set; }

    /// <summary>
    /// Current daily data usage in bytes.
    /// </summary>
    [JsonPropertyName("usageDaily")]
    public long UsageDaily { get; set; }

    /// <summary>
    /// Daily boost data usage in bytes.
    /// </summary>
    [JsonPropertyName("usageDailyBoost")]
    public long UsageDailyBoost { get; set; }

    /// <summary>
    /// Daily data limit in bytes (0 = unlimited).
    /// </summary>
    [JsonPropertyName("usageDailyLimit")]
    public long UsageDailyLimit { get; set; }

    /// <summary>
    /// Current monthly data usage in bytes.
    /// </summary>
    [JsonPropertyName("usageMonthly")]
    public long UsageMonthly { get; set; }

    /// <summary>
    /// Monthly data limit in bytes (0 = unlimited).
    /// </summary>
    [JsonPropertyName("usageMonthlyLimit")]
    public long UsageMonthlyLimit { get; set; }

    /// <summary>
    /// Day of month when monthly usage resets (1-31).
    /// </summary>
    [JsonPropertyName("usageMonthlyResetDay")]
    public int UsageMonthlyResetDay { get; set; }
}

/// <summary>
/// Rate limiting settings for an adapter.
/// </summary>
public class AdapterRateLimit
{
    /// <summary>
    /// Download rate limit in bits per second (0 = unlimited).
    /// </summary>
    [JsonPropertyName("downloadBitsPerSecond")]
    public long DownloadBitsPerSecond { get; set; }

    /// <summary>
    /// Upload rate limit in bits per second (0 = unlimited).
    /// </summary>
    [JsonPropertyName("uploadBitsPerSecond")]
    public long UploadBitsPerSecond { get; set; }
}

public class AdapterRateLimitJsonConverter : JsonConverter<AdapterRateLimit>
{
    public override AdapterRateLimit Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var rateLimit))
        {
            return new AdapterRateLimit
            {
                DownloadBitsPerSecond = rateLimit,
                UploadBitsPerSecond = rateLimit
            };
        }

        if (reader.TokenType == JsonTokenType.String &&
            long.TryParse(reader.GetString(), out var stringRateLimit))
        {
            return new AdapterRateLimit
            {
                DownloadBitsPerSecond = stringRateLimit,
                UploadBitsPerSecond = stringRateLimit
            };
        }

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;

            return new AdapterRateLimit
            {
                DownloadBitsPerSecond = TryGetInt64(root, "downloadBps", "downloadBitsPerSecond"),
                UploadBitsPerSecond = TryGetInt64(root, "uploadBps", "uploadBitsPerSecond")
            };
        }

        return new AdapterRateLimit();
    }

    public override void Write(Utf8JsonWriter writer, AdapterRateLimit value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("downloadBitsPerSecond", value.DownloadBitsPerSecond);
        writer.WriteNumber("uploadBitsPerSecond", value.UploadBitsPerSecond);
        writer.WriteEndObject();
    }

    private static long TryGetInt64(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var property))
            {
                continue;
            }

            return property.ValueKind switch
            {
                JsonValueKind.Number when property.TryGetInt64(out var value) => value,
                JsonValueKind.String when long.TryParse(property.GetString(), out var value) => value,
                _ => 0
            };
        }

        return 0;
    }
}

/// <summary>
/// Directional settings for upload/download behavior.
/// </summary>
public class AdapterDirectionalSettings
{
    /// <summary>
    /// Upload behavior: "on", "backup_off", or "strict_off".
    /// </summary>
    [JsonPropertyName("upload")]
    public string Upload { get; set; } = "on";

    /// <summary>
    /// Download behavior: "on", "backup_off", or "strict_off".
    /// </summary>
    [JsonPropertyName("download")]
    public string Download { get; set; } = "on";
}

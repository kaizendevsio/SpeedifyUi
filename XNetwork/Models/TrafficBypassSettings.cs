using System.Text.Json.Serialization;

namespace XNetwork.Models;

public sealed class TrafficBypassSettings
{
    public List<TrafficBypassRule> Rules { get; set; } = new();

    public string ApplyHelperPath { get; set; } = "/usr/local/sbin/xnetwork-traffic-bypass-apply";

    public int CommandTimeoutSeconds { get; set; } = 15;
}

public sealed class TrafficBypassRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string DisplayName { get; set; } = "";

    public bool Enabled { get; set; } = true;

    public List<string> Destinations { get; set; } = new();

    /// <summary>
    /// Domains whose live DNS answers are collected into an nftables set by dnsmasq and
    /// matched by this rule. Needed for services like TikTok that resolve into shared CDN
    /// space, where a static CIDR would either miss most traffic or divert unrelated hosts.
    /// Matching is suffix-based: "tiktok.com" also covers "www.tiktok.com".
    /// </summary>
    public List<string> Domains { get; set; } = new();

    public string Protocol { get; set; } = TrafficBypassProtocols.Any;

    public List<string> Ports { get; set; } = new();

    public string EgressMode { get; set; } = TrafficBypassEgressModes.AutoPhysical;

    public string InterfaceName { get; set; } = "";
}

public sealed class TrafficBypassValidationResult
{
    public bool IsValid => Errors.Count == 0;

    public List<string> Errors { get; } = new();
}

public sealed record TrafficBypassApplyStatus(
    bool IsSupported,
    bool Applied,
    string Message,
    string? Error,
    DateTimeOffset UpdatedAtUtc);

public sealed record TrafficBypassEgressAdapter(
    string InterfaceName,
    string DisplayName,
    string State);

public static class TrafficBypassProtocols
{
    public const string Any = "any";
    public const string Tcp = "tcp";
    public const string Udp = "udp";

    public static string Normalize(string? value)
    {
        value = value?.Trim().ToLowerInvariant();
        return value is Tcp or Udp ? value : Any;
    }

    public static bool IsKnown(string? value) => Normalize(value) == value?.Trim().ToLowerInvariant();
}

/// <summary>Ready-made rules for services that cannot be expressed as CIDRs.</summary>
public static class TrafficBypassPresets
{
    /// <summary>
    /// TikTok resolves into shared Akamai space plus a DITO carrier cache, so it can only
    /// be bypassed by domain. The API, shop, and SDK hosts matter as much as the CDN ones:
    /// region and currency are decided there, not on the video path. The shop and pangle
    /// families were added after live DNS on the router showed the app actually reaching
    /// <c>pangle.io</c> and <c>tiktokpangle.us</c> while a shorter list left TikTok Shop
    /// still priced in SGD.
    /// </summary>
    public static TrafficBypassRule TikTok() => new()
    {
        DisplayName = "TikTok",
        Enabled = true,
        Domains =
        [
            // Core app and API
            "tiktok.com",
            "tiktokv.com",
            "tiktokv.us",
            "tiktokapi.com",
            // TikTok Shop decides the currency and the deliver-to-region check
            "tiktokglobalshop.com",
            "tiktokshop.com",
            // Content delivery
            "tiktokcdn.com",
            "tiktokcdn-us.com",
            "tiktokcdn-eu.com",
            "ttwstatic.com",
            "ttlivecdn.com",
            // Pangle: the SDK/ad network the app talks to constantly, observed live
            "pangle.io",
            "tiktokpangle.us",
            "pangleglobal.com",
            // CDN CNAME targets observed in live queries; without them the video edges
            // resolve outside every listed zone and ride the tunnel
            "bytefcdn-oversea.com",
            "ovscdns.net",
            "tiktokcdn.com.cdn20.com",
            "hypstarcdn.com",
            "rocket-cdn.com",
            // ByteDance shared infrastructure
            "byteoversea.com",
            "byteoversea.net",
            "byteintlapi.com",
            "bytefcdn.com",
            "ibytedtos.com",
            "ibyteimg.com",
            "byteimg.com",
            "isnssdk.com",
            // Legacy musical.ly estate still referenced by the apps
            "muscdn.com",
            "musical.ly"
        ],
        Protocol = TrafficBypassProtocols.Any,
        EgressMode = TrafficBypassEgressModes.AutoPhysical
    };
}

public static class TrafficBypassEgressModes
{
    public const string AutoPhysical = "auto";
    public const string Interface = "interface";

    public static string Normalize(string? value)
    {
        value = value?.Trim().ToLowerInvariant();
        return value == Interface ? Interface : AutoPhysical;
    }
}

public sealed class TrafficBypassHelperPayload
{
    [JsonPropertyName("rules")]
    public List<TrafficBypassRule> Rules { get; set; } = new();
}

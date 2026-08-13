namespace XNetwork.Models;

/// <summary>
/// Upstream identity observed by probing an ISP lookup endpoint through one specific adapter.
/// </summary>
public sealed class AdapterIdentity
{
    public string InterfaceName { get; init; } = "";

    /// <summary>Public IP as seen by the lookup endpoint through this adapter.</summary>
    public string? PublicIp { get; init; }

    /// <summary>Raw ISP string reported by the endpoint.</summary>
    public string? Isp { get; init; }

    /// <summary>Raw organization string reported by the endpoint.</summary>
    public string? Organization { get; init; }

    /// <summary>Autonomous system label, for example "AS10139" or "AS10139 Smart Communications".</summary>
    public string? AsLabel { get; init; }

    public string? City { get; init; }

    public string? Region { get; init; }

    public string? Country { get; init; }

    public string? CountryCode { get; init; }

    /// <summary>Normalized, display-ready provider name derived from <see cref="Isp"/> or <see cref="Organization"/>.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Endpoint that answered, for example "https://ipwho.is/".</summary>
    public string? Source { get; init; }

    /// <summary>Gateway the adapter used when this identity was captured; used to invalidate on modem swaps.</summary>
    public string? Gateway { get; init; }

    public DateTimeOffset? UpdatedAtUtc { get; init; }

    public string? Error { get; init; }

    public bool IsAvailable => !string.IsNullOrWhiteSpace(DisplayName) || !string.IsNullOrWhiteSpace(PublicIp);

    public string? Location
    {
        get
        {
            var parts = new[] { City, Region, Country }
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .ToArray();

            return parts.Length == 0 ? null : string.Join(", ", parts);
        }
    }

    public static AdapterIdentity Unavailable(
        string interfaceName,
        string error,
        DateTimeOffset updatedAtUtc,
        string? gateway = null) => new()
    {
        InterfaceName = interfaceName,
        Gateway = gateway,
        Error = error,
        UpdatedAtUtc = updatedAtUtc
    };
}

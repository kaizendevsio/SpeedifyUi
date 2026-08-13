namespace XNetwork.Models;

/// <summary>Enforcement state for the router's Wi-Fi adapters, as shown in Settings.</summary>
public sealed class WifiControlStatus
{
    public bool IsSupported { get; init; }

    public string? Message { get; init; }

    public IReadOnlyList<WifiControlInterfaceStatus> Interfaces { get; init; } = Array.Empty<WifiControlInterfaceStatus>();
}

public sealed class WifiControlInterfaceStatus
{
    public string InterfaceName { get; init; } = "";

    public bool IsDisabled { get; init; }

    public string DeviceState { get; init; } = "unknown";

    public DateTimeOffset? LastAppliedUtc { get; init; }

    public string? LastError { get; init; }

    /// <summary>How many times enforcement had to put this adapter back into a disconnected state.</summary>
    public int ReassertCount { get; init; }
}

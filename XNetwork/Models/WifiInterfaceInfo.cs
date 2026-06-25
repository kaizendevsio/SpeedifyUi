namespace XNetwork.Models;

public sealed class WifiInterfaceInfo
{
    public string InterfaceName { get; set; } = "";

    public string State { get; set; } = "unknown";

    public string? ConnectionName { get; set; }

    public bool IsConnected => string.Equals(State, "connected", StringComparison.OrdinalIgnoreCase);

    public string DisplayName => string.IsNullOrWhiteSpace(ConnectionName)
        ? InterfaceName
        : $"{ConnectionName} ({InterfaceName})";
}

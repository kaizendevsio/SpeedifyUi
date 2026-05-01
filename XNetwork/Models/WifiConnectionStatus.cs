namespace XNetwork.Models;

public class WifiConnectionStatus
{
    public bool IsSupported { get; set; }

    public string InterfaceName { get; set; } = "wlan0";

    public string State { get; set; } = "unknown";

    public string? ConnectionName { get; set; }

    public string? Ssid { get; set; }

    public int? Signal { get; set; }

    public string? Security { get; set; }

    public string? Message { get; set; }
}

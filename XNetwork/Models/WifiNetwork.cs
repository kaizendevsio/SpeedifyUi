namespace XNetwork.Models;

public class WifiNetwork
{
    public string Ssid { get; set; } = "";

    public int Signal { get; set; }

    public string Security { get; set; } = "";

    public bool IsActive { get; set; }

    public bool RequiresPassword => !string.IsNullOrWhiteSpace(Security) && Security != "--";
}

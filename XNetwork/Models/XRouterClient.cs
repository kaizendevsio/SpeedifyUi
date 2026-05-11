namespace XNetwork.Models;

public class XRouterClient
{
    public string Hostname { get; set; } = "";

    public string IpAddress { get; set; } = "";

    public string MacAddress { get; set; } = "";

    public string ConnectionType { get; set; } = "Unknown";

    public double UploadMbps { get; set; }

    public double DownloadMbps { get; set; }

    public int? SignalDbm { get; set; }

    public string OnlineDuration { get; set; } = "";

    public bool InternetAllowed { get; set; } = true;

    public bool VpnEnabled { get; set; }

    public bool DnsFilterEnabled { get; set; } = true;

    public bool IsProtected { get; set; }
}

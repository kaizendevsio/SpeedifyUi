namespace XNetwork.Models;

public class XBondSettings
{
    public bool Enabled { get; set; }

    public string ClientBinaryPath { get; set; } = "xbond-client";

    public string ClientConfigPath { get; set; } = "/etc/xbond/client.toml";

    public int StatusTimeoutSeconds { get; set; } = 2;
}

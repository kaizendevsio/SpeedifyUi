namespace XNetwork.Models;

public class XBondSettings
{
    public bool Enabled { get; set; }

    public string ClientBinaryPath { get; set; } = "xbond-client";

    public string ClientConfigPath { get; set; } = "/etc/xbond/client.toml";

    public int StatusTimeoutSeconds { get; set; } = 2;

    public string PublicTestServerAddress { get; set; } = "45.77.241.247:8444";

    public int PublicTestBypassPort { get; set; } = 8444;

    public string PublicTestBypassProtocol { get; set; } = "udp";

    public int PublicTestPathId { get; set; } = 1;

    public List<int> PublicTestPathIds { get; set; } = new();

    public List<string> PublicTestBinds { get; set; } = new();

    public int PublicTestCount { get; set; } = 10;

    public int PublicTestIntervalMs { get; set; } = 100;

    public int PublicTestPacketTimeoutMs { get; set; } = 2500;

    public int PublicTestBypassSettleMs { get; set; } = 2000;

    public int PublicTestCommandTimeoutSeconds { get; set; } = 45;

    public string PublicTestKeyEnvironmentVariable { get; set; } = "XBOND_PSK";

    public string PublicTestKeyFilePath { get; set; } = "/home/xeon-network/.config/XNetwork/xbond-psk";
}

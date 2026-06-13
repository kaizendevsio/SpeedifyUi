namespace XNetwork.Models;

public class XBondSettings
{
    public bool Enabled { get; set; }

    public string TrafficEngineMode { get; set; } = XBondTrafficEngineModes.SpeedifyPrimary;

    public bool AllowServiceControl { get; set; }

    public bool AllowPrimaryMode { get; set; }

    public string ClientServiceName { get; set; } = "xbond-client.service";

    public string ServiceManagerPath { get; set; } = "systemctl";

    public int ServiceCommandTimeoutSeconds { get; set; } = 10;

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

    public bool MultiPathUseSpeedifyBypass { get; set; }

    public int PublicTestCommandTimeoutSeconds { get; set; } = 45;

    public string PublicTestKeyEnvironmentVariable { get; set; } = "XBOND_PSK";

    public string PublicTestKeyFilePath { get; set; } = "/home/xeon-network/.config/XNetwork/xbond-psk";
}

public static class XBondTrafficEngineModes
{
    public const string SpeedifyPrimary = "speedify-primary";
    public const string XBondCanary = "xbond-canary";
    public const string XBondPrimary = "xbond-primary";

    public static bool IsKnown(string mode)
    {
        return string.Equals(mode, SpeedifyPrimary, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(mode, XBondCanary, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(mode, XBondPrimary, StringComparison.OrdinalIgnoreCase);
    }

    public static string Normalize(string? mode)
    {
        if (string.Equals(mode, XBondCanary, StringComparison.OrdinalIgnoreCase))
        {
            return XBondCanary;
        }

        if (string.Equals(mode, XBondPrimary, StringComparison.OrdinalIgnoreCase))
        {
            return XBondPrimary;
        }

        return SpeedifyPrimary;
    }
}

public class XBondTrafficEngineStatus
{
    public string Mode { get; set; } = XBondTrafficEngineModes.SpeedifyPrimary;

    public bool ServiceControlAllowed { get; set; }

    public bool PrimaryModeAllowed { get; set; }

    public string ClientServiceName { get; set; } = "";

    public string ClientServiceState { get; set; } = "unknown";

    public bool ClientServiceRunning { get; set; }

    public bool CanStartCanary => ServiceControlAllowed && Mode == XBondTrafficEngineModes.XBondCanary && !ClientServiceRunning;

    public bool CanStopCanary => ServiceControlAllowed && ClientServiceRunning;

    public string Message { get; set; } = "";

    public string? Error { get; set; }

    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

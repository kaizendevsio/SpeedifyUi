namespace XNetwork.Models;

public sealed class StarlinkLanAccessSettings
{
    public bool Enabled { get; set; } = true;

    public string Destination { get; set; } = "192.168.100.1";

    public string LanInterface { get; set; } = "eth0";

    public string LanSubnet { get; set; } = "192.168.145.0/24";

    public string ApplyHelperPath { get; set; } = "/usr/local/sbin/xnetwork-starlink-lan-access-apply";

    public int CheckIntervalSeconds { get; set; } = 10;

    public int VerifyIntervalSeconds { get; set; } = 60;

    public int CommandTimeoutSeconds { get; set; } = 10;

    public TimeSpan CheckInterval => TimeSpan.FromSeconds(Math.Clamp(CheckIntervalSeconds, 5, 300));

    public TimeSpan VerifyInterval => TimeSpan.FromSeconds(Math.Clamp(VerifyIntervalSeconds, 15, 600));
}

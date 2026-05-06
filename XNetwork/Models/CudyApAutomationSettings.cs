namespace XNetwork.Models;

public class CudyApAutomationSettings
{
    public bool Enabled { get; set; }

    public string ManagementBaseUrl { get; set; } = "";

    public string AdminPassword { get; set; } = "";

    public string AdminPasswordFilePath { get; set; } = "";

    public string AdminPasswordEnvironmentVariable { get; set; } = "CUDY_ADMIN_PASSWORD";

    public string WifiInterface { get; set; } = "wlan0";

    public string HomeSsid { get; set; } = "";

    public string HomeBssid { get; set; } = "";

    public int DisableWhenSignalAtLeast { get; set; } = 55;

    public int EnableWhenSignalBelow { get; set; } = 35;

    public int CheckIntervalSeconds { get; set; } = 15;

    public int DisableAfterSeenSeconds { get; set; } = 30;

    public int EnableAfterMissingSeconds { get; set; } = 120;

    public bool ReEnableWhenHomeMissing { get; set; } = true;

    public bool Disable2G { get; set; } = true;

    public bool Disable5G { get; set; } = true;

    public int RequestTimeoutSeconds { get; set; } = 10;
}

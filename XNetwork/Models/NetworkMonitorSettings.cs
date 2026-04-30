namespace XNetwork.Models;

public class NetworkMonitorSettings
{
    /// <summary>
    /// List of network interface names that are allowed to be monitored and restarted
    /// </summary>
    public List<string> WhitelistedLinks { get; set; } = new();
    
    /// <summary>
    /// Time in seconds before attempting to restart a down link
    /// Default is 30 seconds
    /// </summary>
    public int DownTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Maximum number of restart attempts for a link before cooldown is applied. Set 0 to keep retrying indefinitely.
    /// </summary>
    public int MaxRestartAttemptsPerHour { get; set; }

    /// <summary>
    /// Time to suppress additional restart attempts after the hourly limit is hit.
    /// </summary>
    public int RestartCooldownMinutes { get; set; } = 15;
    
    /// <summary>
    /// Whether the network monitor service is enabled
    /// </summary>
    public bool Enabled { get; set; } = true;
}

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
    /// Whether the network monitor service is enabled
    /// </summary>
    public bool Enabled { get; set; } = true;
}

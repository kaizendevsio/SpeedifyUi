namespace XNetwork.Models;

/// <summary>
/// Represents OS-level network adapter routing priority settings.
/// Used to control which adapter the OS prefers for bypassed traffic (traffic not going through Speedify).
/// </summary>
public class AdapterRoutingPriority
{
    /// <summary>
    /// The Linux network interface name (e.g., "eth0", "wlan0", "enp0s3").
    /// </summary>
    public string InterfaceName { get; set; } = "";

    /// <summary>
    /// A user-friendly display name for the adapter.
    /// </summary>
    public string DisplayName { get; set; } = "";

    /// <summary>
    /// The routing metric value. Lower values mean higher priority.
    /// Default is 100. Values typically range from 1-9999.
    /// </summary>
    public int Metric { get; set; } = 100;

    /// <summary>
    /// The gateway IP address for this interface (e.g., "192.168.1.1").
    /// </summary>
    public string? Gateway { get; set; }

    /// <summary>
    /// The IP address assigned to this interface.
    /// </summary>
    public string? IpAddress { get; set; }

    /// <summary>
    /// Indicates if this adapter is currently active/up.
    /// </summary>
    public bool IsUp { get; set; }

    /// <summary>
    /// The current metric value from the system (before any user changes).
    /// </summary>
    public int CurrentSystemMetric { get; set; }
}

/// <summary>
/// Configuration section for adapter routing priority settings stored in appsettings.json.
/// </summary>
public class AdapterRoutingPriorityConfig
{
    /// <summary>
    /// Whether the adapter routing priority feature is enabled.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Whether to apply saved metrics on application startup.
    /// </summary>
    public bool ApplyOnStartup { get; set; } = false;

    /// <summary>
    /// List of adapter metric configurations to persist.
    /// </summary>
    public List<AdapterMetricSetting> AdapterMetrics { get; set; } = new();
}

/// <summary>
/// Represents a saved adapter metric setting.
/// </summary>
public class AdapterMetricSetting
{
    /// <summary>
    /// The Linux network interface name.
    /// </summary>
    public string InterfaceName { get; set; } = "";

    /// <summary>
    /// The preferred routing metric value.
    /// </summary>
    public int Metric { get; set; } = 100;
}
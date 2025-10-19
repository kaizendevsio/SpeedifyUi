using XNetwork.Models;

namespace XNetwork.Services;

/// <summary>
/// Service for monitoring connection health metrics in real-time
/// </summary>
public interface IConnectionHealthService
{
    /// <summary>
    /// Gets the overall connection health across all active adapters
    /// </summary>
    /// <returns>Overall connection health status</returns>
    ConnectionHealth GetOverallHealth();

    /// <summary>
    /// Gets health metrics for a specific adapter
    /// </summary>
    /// <param name="adapterId">Adapter identifier</param>
    /// <returns>Health metrics if adapter exists, null otherwise</returns>
    HealthMetrics? GetAdapterHealth(string adapterId);

    /// <summary>
    /// Gets health metrics for all active adapters
    /// </summary>
    /// <returns>Dictionary of adapter ID to health metrics</returns>
    Dictionary<string, HealthMetrics> GetAllAdapterHealth();

    /// <summary>
    /// Checks if the service has collected enough data to report accurate health
    /// </summary>
    /// <returns>True if initialized with sufficient data</returns>
    bool IsInitialized();
}
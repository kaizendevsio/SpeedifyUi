using UnitsNet;

namespace XNetwork.Utils;

/// <summary>
/// Provides dynamic speed formatting based on magnitude.
/// Shows speeds in kbps for values below 1 Mbps and Mbps for higher values.
/// </summary>
public static class SpeedFormatter
{
    /// <summary>
    /// Formats a speed value (in Mbps) to appropriate units with proper precision.
    /// </summary>
    /// <param name="speedMbps">Speed value in megabits per second</param>
    /// <returns>Formatted string with value and unit (e.g., "150.5 kbps" or "5.2 Mbps")</returns>
    public static string FormatSpeed(double speedMbps)
    {
        // Handle edge cases
        if (speedMbps <= 0)
            return "0 kbps";

        // Create BitRate from Mbps
        var bitRate = BitRate.FromMegabitsPerSecond(speedMbps);
        
        // Use kbps for speeds below 1 Mbps, otherwise use Mbps
        if (speedMbps < 1.0)
        {
            var kbps = bitRate.KilobitsPerSecond;
            return $"{Math.Round(kbps, 1)} kbps";
        }
        else
        {
            return $"{Math.Round(speedMbps, 1)} Mbps";
        }
    }
    
    /// <summary>
    /// Formats a speed value (in Mbps) returning just the numeric value.
    /// </summary>
    /// <param name="speedMbps">Speed value in megabits per second</param>
    /// <returns>Formatted numeric value as string</returns>
    public static string FormatSpeedValue(double speedMbps)
    {
        if (speedMbps <= 0)
            return "0";

        var bitRate = BitRate.FromMegabitsPerSecond(speedMbps);
        
        if (speedMbps < 1.0)
        {
            var kbps = bitRate.KilobitsPerSecond;
            return Math.Round(kbps, 1).ToString();
        }
        else
        {
            return Math.Round(speedMbps, 1).ToString();
        }
    }
    
    /// <summary>
    /// Gets the unit suffix for a speed value.
    /// </summary>
    /// <param name="speedMbps">Speed value in megabits per second</param>
    /// <returns>Unit suffix ("kbps" or "Mbps")</returns>
    public static string GetSpeedUnit(double speedMbps)
    {
        return speedMbps < 1.0 ? "kbps" : "Mbps";
    }
}
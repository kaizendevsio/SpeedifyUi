using XNetwork.Models;

namespace XNetwork.Utils;

/// <summary>
/// Display formatters shared by the dashboard, the adapter details sheet, and the Starlink details
/// section. Values that are missing or not yet measured render as "--" rather than zero.
/// </summary>
public static class TelemetryFormatter
{
    public static string FormatLatency(double? value)
    {
        return value.HasValue && value.Value > 0 ? $"{Math.Round(value.Value)} ms" : "--";
    }

    public static string FormatPercent(double? value)
    {
        return value.HasValue ? $"{Math.Round(value.Value, 1)}%" : "--";
    }

    public static int GetPercentDecimals(double value)
    {
        return Math.Abs(value - Math.Round(value)) < 0.05 ? 0 : 1;
    }

    public static string FormatNullableMs(double? milliseconds)
    {
        return milliseconds is > 0 ? $"{Math.Round(milliseconds.Value)} ms" : "--";
    }

    public static string FormatNullableSpeed(double? speedMbps)
    {
        return speedMbps is >= 0 ? SpeedFormatter.FormatSpeed(speedMbps.Value) : "--";
    }

    public static string FormatNullablePercent(double? percent)
    {
        return percent is >= 0 ? $"{Math.Round(percent.Value, percent.Value < 1 ? 2 : 1)}%" : "--";
    }

    public static string FormatNullableDegrees(double? degrees)
    {
        return degrees is >= 0 ? $"{Math.Round(degrees.Value, 1)} deg" : "--";
    }

    public static string FormatDuration(long? totalSeconds)
    {
        if (totalSeconds is null or < 0)
        {
            return "--";
        }

        var duration = TimeSpan.FromSeconds(totalSeconds.Value);
        if (duration.TotalDays >= 1)
        {
            return $"{(int)duration.TotalDays}d {duration.Hours}h";
        }

        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        }

        return $"{Math.Max(1, duration.Minutes)}m";
    }

    public static string FormatDropRate(double? dropRate)
    {
        return dropRate is >= 0 ? $"{Math.Round(dropRate.Value * 100, 2)}%" : "--";
    }

    public static string FormatObstructionState(bool? currentlyObstructed)
    {
        return currentlyObstructed switch
        {
            true => "Currently obstructed",
            false => "Clear",
            _ => "Unknown"
        };
    }

    public static string FormatGpsState(StarlinkTelemetrySnapshot snapshot)
    {
        return snapshot.GpsValid switch
        {
            true => "Valid",
            false => "No fix",
            _ => "--"
        };
    }

    public static string FormatGpsSatellites(int? satellites)
    {
        return satellites.HasValue ? $"{satellites.Value} satellites" : "--";
    }

    public static string FormatLastUpdated(DateTimeOffset? updatedUtc)
    {
        if (updatedUtc is null)
        {
            return "--";
        }

        var age = DateTimeOffset.UtcNow - updatedUtc.Value;
        if (age.TotalSeconds < 60)
        {
            return $"{Math.Max(0, (int)age.TotalSeconds)}s ago";
        }

        if (age.TotalMinutes < 60)
        {
            return $"{(int)age.TotalMinutes}m ago";
        }

        return updatedUtc.Value.ToLocalTime().ToString("HH:mm");
    }

    public static string FormatShortDeviceId(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return "--";
        }

        return deviceId.Length <= 12 ? deviceId : $"{deviceId[..6]}...{deviceId[^4..]}";
    }

    public static string FormatUnknown(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "--" : value;
    }
}

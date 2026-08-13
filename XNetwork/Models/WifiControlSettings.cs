namespace XNetwork.Models;

/// <summary>Which Wi-Fi adapters are blocked from connecting, and how often that is re-asserted.</summary>
public sealed class WifiControlSettings
{
    /// <summary>Interface name to disabled flag. A missing interface means enabled.</summary>
    public Dictionary<string, bool> DisabledInterfaces { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public int EnforcementIntervalSeconds { get; set; } = 30;

    public TimeSpan EnforcementInterval => TimeSpan.FromSeconds(Math.Clamp(EnforcementIntervalSeconds, 10, 3600));

    public bool IsDisabled(string interfaceName) =>
        !string.IsNullOrWhiteSpace(interfaceName) &&
        DisabledInterfaces.TryGetValue(interfaceName, out var disabled) &&
        disabled;
}

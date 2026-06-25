namespace XNetwork.Models;

public sealed class LocalDeviceProxySettings
{
    public List<LocalDeviceProxyEntry> Entries { get; set; } = new();
}

public static class LocalDeviceProxyModes
{
    public const string Port = "port";
    public const string Route = "route";

    public static string Normalize(string? mode)
    {
        return string.Equals(mode, Port, StringComparison.OrdinalIgnoreCase)
            ? Port
            : Route;
    }
}

public sealed class LocalDeviceProxyEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string DisplayName { get; set; } = "";

    public string ProxyMode { get; set; } = LocalDeviceProxyModes.Route;

    public int? ListenPort { get; set; }

    public string ExposedRoute { get; set; } = "";

    public string TargetUrl { get; set; } = "";

    public bool Enabled { get; set; } = true;

    public bool TelemetryEnabled { get; set; } = true;
}

public sealed class LocalDeviceProxyValidationResult
{
    public bool IsValid => Errors.Count == 0;

    public List<string> Errors { get; } = new();
}

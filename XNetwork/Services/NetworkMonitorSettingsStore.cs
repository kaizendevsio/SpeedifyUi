using System.Text.Json;
using System.Text.Json.Serialization;
using XNetwork.Models;

namespace XNetwork.Services;

public class NetworkMonitorSettingsStore
{
    private const string SettingsFileName = "network-monitor-settings.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ILogger<NetworkMonitorSettingsStore> _logger;
    private readonly string _filePath;
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public NetworkMonitorSettingsStore(IHostEnvironment environment, ILogger<NetworkMonitorSettingsStore> logger)
        : this(logger, Path.Combine(GetAppDataDirectory(environment), SettingsFileName))
    {
    }

    public NetworkMonitorSettingsStore(ILogger<NetworkMonitorSettingsStore> logger, string filePath)
    {
        _logger = logger;
        _filePath = filePath;
    }

    public void Load(NetworkMonitorSettings settings)
    {
        if (!File.Exists(_filePath))
        {
            return;
        }

        try
        {
            var persistedSettings = JsonSerializer.Deserialize<NetworkMonitorSettings>(File.ReadAllText(_filePath), JsonOptions);
            if (persistedSettings == null)
            {
                return;
            }

            Apply(persistedSettings, settings);
            _logger.LogInformation("Loaded persisted network monitor settings from {Path}", _filePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load persisted network monitor settings from {Path}", _filePath);
        }
    }

    public async Task SaveAsync(NetworkMonitorSettings settings, CancellationToken cancellationToken = default)
    {
        Validate(settings);
        var normalized = Normalize(settings);
        await _saveLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(normalized, JsonOptions);
            var temporaryPath = _filePath + ".tmp";
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken).ConfigureAwait(false);
            RestrictOwnerAccess(temporaryPath);
            File.Move(temporaryPath, _filePath, overwrite: true);
            RestrictOwnerAccess(_filePath);
            _logger.LogInformation("Persisted network monitor settings to {Path}", _filePath);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    private static void Apply(NetworkMonitorSettings source, NetworkMonitorSettings target)
    {
        var normalized = Normalize(source);
        target.Enabled = normalized.Enabled;
        target.WhitelistedLinks = normalized.WhitelistedLinks;
        target.AdapterAliases = normalized.AdapterAliases;
        target.DownTimeoutSeconds = normalized.DownTimeoutSeconds;
        target.MaxRestartAttemptsPerHour = normalized.MaxRestartAttemptsPerHour;
        target.RestartCooldownMinutes = normalized.RestartCooldownMinutes;
    }

    public static NetworkMonitorSettings Normalize(NetworkMonitorSettings settings) => new()
    {
        Enabled = settings.Enabled,
        WhitelistedLinks = (settings.WhitelistedLinks ?? [])
            .Select(link => link.Trim())
            .Where(XBondPhysicalPathProbeService.IsSafePhysicalInterface)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList(),
        AdapterAliases = (settings.AdapterAliases ?? new Dictionary<string, string>())
            .Where(pair => XBondPhysicalPathProbeService.IsSafePhysicalInterface(pair.Key))
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .ToDictionary(pair => pair.Key.Trim(), pair => pair.Value.Trim(), StringComparer.OrdinalIgnoreCase),
        DownTimeoutSeconds = Math.Clamp(settings.DownTimeoutSeconds, 5, 300),
        MaxRestartAttemptsPerHour = Math.Max(0, settings.MaxRestartAttemptsPerHour),
        RestartCooldownMinutes = Math.Max(1, settings.RestartCooldownMinutes)
    };

    public static void Validate(NetworkMonitorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        foreach (var interfaceName in settings.WhitelistedLinks ?? [])
        {
            if (!XBondPhysicalPathProbeService.IsSafePhysicalInterface(interfaceName))
            {
                throw new InvalidOperationException($"Invalid Linux interface name: {interfaceName}");
            }
        }

        foreach (var (interfaceName, alias) in settings.AdapterAliases ?? new Dictionary<string, string>())
        {
            if (!XBondPhysicalPathProbeService.IsSafePhysicalInterface(interfaceName))
            {
                throw new InvalidOperationException($"Invalid Linux interface name: {interfaceName}");
            }

            if (alias.Trim().Length > 48 || alias.Any(char.IsControl))
            {
                throw new InvalidOperationException($"Adapter alias for {interfaceName} must be 48 characters or fewer and cannot contain control characters.");
            }
        }
    }

    public static string? GetAdapterAlias(NetworkMonitorSettings settings, string interfaceName) =>
        settings.AdapterAliases.FirstOrDefault(pair =>
            string.Equals(pair.Key, interfaceName, StringComparison.OrdinalIgnoreCase)).Value is { Length: > 0 } alias
                ? alias
                : null;

    private static string GetAppDataDirectory(IHostEnvironment environment)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
        {
            appData = Path.Combine(environment.ContentRootPath, "App_Data");
        }

        return Path.Combine(appData, "XNetwork");
    }

    private static void RestrictOwnerAccess(string path)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}

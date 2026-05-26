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
        await _saveLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(settings, JsonOptions);
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
        target.Enabled = source.Enabled;
        target.WhitelistedLinks = source.WhitelistedLinks ?? new List<string>();
        target.DownTimeoutSeconds = source.DownTimeoutSeconds;
        target.MaxRestartAttemptsPerHour = source.MaxRestartAttemptsPerHour;
        target.RestartCooldownMinutes = source.RestartCooldownMinutes;
    }

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

using System.Text.Json;
using XNetwork.Models;

namespace XNetwork.Services;

/// <summary>Persists which Wi-Fi adapters are blocked from connecting.</summary>
public sealed class WifiControlSettingsStore
{
    private const string SettingsFileName = "wifi-control-settings.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly ILogger<WifiControlSettingsStore> _logger;
    private readonly string _filePath;
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public WifiControlSettingsStore(IHostEnvironment environment, ILogger<WifiControlSettingsStore> logger)
        : this(logger, Path.Combine(GetAppDataDirectory(environment), SettingsFileName))
    {
    }

    public WifiControlSettingsStore(ILogger<WifiControlSettingsStore> logger, string filePath)
    {
        _logger = logger;
        _filePath = filePath;
    }

    public void Load(WifiControlSettings settings)
    {
        if (!File.Exists(_filePath))
        {
            return;
        }

        try
        {
            var persisted = JsonSerializer.Deserialize<WifiControlSettings>(File.ReadAllText(_filePath), JsonOptions);
            if (persisted is null)
            {
                return;
            }

            settings.DisabledInterfaces = new Dictionary<string, bool>(
                persisted.DisabledInterfaces ?? new Dictionary<string, bool>(),
                StringComparer.OrdinalIgnoreCase);

            if (persisted.EnforcementIntervalSeconds > 0)
            {
                settings.EnforcementIntervalSeconds = persisted.EnforcementIntervalSeconds;
            }

            _logger.LogInformation("Loaded persisted Wi-Fi control settings from {Path}", _filePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load Wi-Fi control settings from {Path}", _filePath);
        }
    }

    public async Task SaveAsync(WifiControlSettings settings, CancellationToken cancellationToken = default)
    {
        await _saveLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporaryPath = _filePath + ".tmp";
            await File.WriteAllTextAsync(
                    temporaryPath,
                    JsonSerializer.Serialize(settings, JsonOptions),
                    cancellationToken)
                .ConfigureAwait(false);
            RestrictOwnerAccess(temporaryPath);
            File.Move(temporaryPath, _filePath, overwrite: true);
            RestrictOwnerAccess(_filePath);
        }
        finally
        {
            _saveLock.Release();
        }
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
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;
using XNetwork.Models;

namespace XNetwork.Services;

public class PrivateReconnectSettingsStore
{
    private const string SettingsFileName = "private-reconnect-settings.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ILogger<PrivateReconnectSettingsStore> _logger;
    private readonly string _filePath;
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public PrivateReconnectSettingsStore(IHostEnvironment environment, ILogger<PrivateReconnectSettingsStore> logger)
        : this(logger, Path.Combine(GetAppDataDirectory(environment), SettingsFileName))
    {
    }

    public PrivateReconnectSettingsStore(ILogger<PrivateReconnectSettingsStore> logger, string filePath)
    {
        _logger = logger;
        _filePath = filePath;
    }

    public void Load(PrivateReconnectSettings settings)
    {
        if (!File.Exists(_filePath))
        {
            return;
        }

        try
        {
            var persistedSettings = JsonSerializer.Deserialize<PrivateReconnectSettings>(File.ReadAllText(_filePath), JsonOptions);
            if (persistedSettings == null)
            {
                return;
            }

            Apply(persistedSettings, settings);
            _logger.LogInformation("Loaded persisted private reconnect settings from {Path}", _filePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load persisted private reconnect settings from {Path}", _filePath);
        }
    }

    public async Task SaveAsync(PrivateReconnectSettings settings, CancellationToken cancellationToken = default)
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
            _logger.LogInformation("Persisted private reconnect settings to {Path}", _filePath);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    private static void Apply(PrivateReconnectSettings source, PrivateReconnectSettings target)
    {
        target.Enabled = source.Enabled;
        target.IntervalMinutes = source.IntervalMinutes;
        target.DelaySeconds = source.DelaySeconds;
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

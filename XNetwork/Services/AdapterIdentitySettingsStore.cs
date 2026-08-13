using System.Text.Json;
using XNetwork.Models;

namespace XNetwork.Services;

/// <summary>
/// Persists the user-facing on/off switch for ISP identity lookups. Endpoints and timings stay in
/// appsettings.json because they are host configuration rather than a user preference.
/// </summary>
public sealed class AdapterIdentitySettingsStore
{
    private const string SettingsFileName = "adapter-identity-settings.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly ILogger<AdapterIdentitySettingsStore> _logger;
    private readonly string _filePath;
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public AdapterIdentitySettingsStore(IHostEnvironment environment, ILogger<AdapterIdentitySettingsStore> logger)
        : this(logger, Path.Combine(GetAppDataDirectory(environment), SettingsFileName))
    {
    }

    public AdapterIdentitySettingsStore(ILogger<AdapterIdentitySettingsStore> logger, string filePath)
    {
        _logger = logger;
        _filePath = filePath;
    }

    public void Load(AdapterIdentitySettings settings)
    {
        if (!File.Exists(_filePath))
        {
            return;
        }

        try
        {
            var persisted = JsonSerializer.Deserialize<PersistedSettings>(File.ReadAllText(_filePath), JsonOptions);
            if (persisted?.Enabled is { } enabled)
            {
                settings.Enabled = enabled;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load adapter identity settings from {Path}", _filePath);
        }
    }

    public async Task SaveAsync(AdapterIdentitySettings settings, CancellationToken cancellationToken = default)
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
                    JsonSerializer.Serialize(new PersistedSettings { Enabled = settings.Enabled }, JsonOptions),
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

    private sealed class PersistedSettings
    {
        public bool? Enabled { get; init; }
    }
}

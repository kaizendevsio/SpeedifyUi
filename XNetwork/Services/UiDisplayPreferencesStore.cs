using System.Text.Json;
using XNetwork.Models;

namespace XNetwork.Services;

public sealed class UiDisplayPreferencesStore
{
    private const string SettingsFileName = "ui-display-preferences.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly ILogger<UiDisplayPreferencesStore> _logger;
    private readonly string _filePath;
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public UiDisplayPreferencesStore(IHostEnvironment environment, ILogger<UiDisplayPreferencesStore> logger)
        : this(logger, Path.Combine(GetAppDataDirectory(environment), SettingsFileName))
    {
    }

    public UiDisplayPreferencesStore(ILogger<UiDisplayPreferencesStore> logger, string filePath)
    {
        _logger = logger;
        _filePath = filePath;
    }

    public void Load(UiDisplayPreferences preferences)
    {
        if (!File.Exists(_filePath))
        {
            return;
        }

        try
        {
            var persisted = JsonSerializer.Deserialize<UiDisplayPreferences>(File.ReadAllText(_filePath), JsonOptions);
            if (persisted is not null)
            {
                preferences.AdapterTechnicalDetails = persisted.AdapterTechnicalDetails;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load UI display preferences from {Path}", _filePath);
        }
    }

    public async Task SaveAsync(UiDisplayPreferences preferences, CancellationToken cancellationToken = default)
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
                    JsonSerializer.Serialize(preferences, JsonOptions),
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

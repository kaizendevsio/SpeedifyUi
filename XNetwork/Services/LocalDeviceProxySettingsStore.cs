using System.Text.Json;
using System.Text.Json.Serialization;
using XNetwork.Models;

namespace XNetwork.Services;

public sealed class LocalDeviceProxySettingsStore
{
    private const string SettingsFileName = "local-device-proxies.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ILogger<LocalDeviceProxySettingsStore> _logger;
    private readonly string _filePath;
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public LocalDeviceProxySettingsStore(IHostEnvironment environment, ILogger<LocalDeviceProxySettingsStore> logger)
        : this(logger, Path.Combine(GetAppDataDirectory(environment), SettingsFileName))
    {
    }

    public LocalDeviceProxySettingsStore(ILogger<LocalDeviceProxySettingsStore> logger, string filePath)
    {
        _logger = logger;
        _filePath = filePath;
    }

    public void Load(LocalDeviceProxySettings settings)
    {
        if (!File.Exists(_filePath))
        {
            return;
        }

        try
        {
            var persisted = JsonSerializer.Deserialize<LocalDeviceProxySettings>(File.ReadAllText(_filePath), JsonOptions);
            if (persisted is null)
            {
                return;
            }

            settings.Entries = NormalizeEntries(persisted.Entries);
            _logger.LogInformation("Loaded local device proxy settings from {Path}", _filePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load local device proxy settings from {Path}", _filePath);
        }
    }

    public async Task SaveAsync(LocalDeviceProxySettings settings, CancellationToken cancellationToken = default)
    {
        await _saveLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            settings.Entries = NormalizeEntries(settings.Entries);
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            var temporaryPath = _filePath + ".tmp";
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken).ConfigureAwait(false);
            RestrictOwnerAccess(temporaryPath);
            File.Move(temporaryPath, _filePath, overwrite: true);
            RestrictOwnerAccess(_filePath);
            _logger.LogInformation("Persisted local device proxy settings to {Path}", _filePath);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    private static List<LocalDeviceProxyEntry> NormalizeEntries(IEnumerable<LocalDeviceProxyEntry>? entries)
    {
        return (entries ?? [])
            .Where(entry => entry is not null)
            .Select(entry => new LocalDeviceProxyEntry
            {
                Id = string.IsNullOrWhiteSpace(entry.Id) ? Guid.NewGuid().ToString("N") : entry.Id.Trim(),
                DisplayName = entry.DisplayName?.Trim() ?? "",
                ExposedRoute = LocalDeviceProxyService.NormalizeRoute(entry.ExposedRoute),
                TargetUrl = LocalDeviceProxyService.NormalizeTargetUrl(entry.TargetUrl),
                Enabled = entry.Enabled,
                TelemetryEnabled = entry.TelemetryEnabled
            })
            .ToList();
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


using System.Text.Json;
using System.Text.Json.Serialization;
using XNetwork.Models;

namespace XNetwork.Services;

public sealed class TrafficBypassSettingsStore
{
    private const string SettingsFileName = "traffic-bypass-rules.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ILogger<TrafficBypassSettingsStore> _logger;
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public TrafficBypassSettingsStore(IHostEnvironment environment, ILogger<TrafficBypassSettingsStore> logger)
        : this(logger, Path.Combine(GetAppDataDirectory(environment), SettingsFileName))
    {
    }

    public TrafficBypassSettingsStore(ILogger<TrafficBypassSettingsStore> logger, string filePath)
    {
        _logger = logger;
        FilePath = filePath;
    }

    public string FilePath { get; }

    public void Load(TrafficBypassSettings settings)
    {
        if (!File.Exists(FilePath))
        {
            return;
        }

        try
        {
            var persisted = JsonSerializer.Deserialize<TrafficBypassSettings>(File.ReadAllText(FilePath), JsonOptions);
            if (persisted is null)
            {
                return;
            }

            settings.Rules = NormalizeRules(persisted.Rules);
            settings.ApplyHelperPath = string.IsNullOrWhiteSpace(persisted.ApplyHelperPath)
                ? settings.ApplyHelperPath
                : persisted.ApplyHelperPath.Trim();
            settings.CommandTimeoutSeconds = Math.Clamp(persisted.CommandTimeoutSeconds, 3, 60);
            _logger.LogInformation("Loaded traffic bypass settings from {Path}", FilePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load traffic bypass settings from {Path}", FilePath);
        }
    }

    public async Task SaveAsync(TrafficBypassSettings settings, CancellationToken cancellationToken = default)
    {
        await _saveLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            settings.Rules = NormalizeRules(settings.Rules);
            settings.ApplyHelperPath = string.IsNullOrWhiteSpace(settings.ApplyHelperPath)
                ? "/usr/local/sbin/xnetwork-traffic-bypass-apply"
                : settings.ApplyHelperPath.Trim();
            settings.CommandTimeoutSeconds = Math.Clamp(settings.CommandTimeoutSeconds, 3, 60);

            var json = JsonSerializer.Serialize(settings, JsonOptions);
            var temporaryPath = FilePath + ".tmp";
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken).ConfigureAwait(false);
            RestrictOwnerAccess(temporaryPath);
            File.Move(temporaryPath, FilePath, overwrite: true);
            RestrictOwnerAccess(FilePath);
            _logger.LogInformation("Persisted traffic bypass settings to {Path}", FilePath);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    private static List<TrafficBypassRule> NormalizeRules(IEnumerable<TrafficBypassRule>? rules) =>
        (rules ?? [])
            .Where(rule => rule is not null)
            .Select(TrafficBypassService.NormalizeRule)
            .ToList();

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

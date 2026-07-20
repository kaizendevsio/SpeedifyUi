using System.Text.Json;
using System.Text.Json.Serialization;
using XNetwork.Models;

namespace XNetwork.Services;

public sealed class XBondClientWatchdogSettingsStore
{
    private const string SettingsFileName = "xbond-client-watchdog-settings.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ILogger<XBondClientWatchdogSettingsStore> _logger;
    private readonly string _filePath;
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public XBondClientWatchdogSettingsStore(
        IHostEnvironment environment,
        ILogger<XBondClientWatchdogSettingsStore> logger)
        : this(logger, Path.Combine(GetAppDataDirectory(environment), SettingsFileName))
    {
    }

    public XBondClientWatchdogSettingsStore(
        ILogger<XBondClientWatchdogSettingsStore> logger,
        string filePath)
    {
        _logger = logger;
        _filePath = filePath;
    }

    public void Load(XBondClientWatchdogSettings settings)
    {
        if (!File.Exists(_filePath))
        {
            Normalize(settings);
            return;
        }

        try
        {
            var persisted = JsonSerializer.Deserialize<XBondClientWatchdogSettings>(
                File.ReadAllText(_filePath),
                JsonOptions);
            if (persisted is not null)
            {
                Apply(persisted, settings);
                _logger.LogInformation("Loaded persisted XBond client watchdog settings from {Path}", _filePath);
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load XBond client watchdog settings from {Path}", _filePath);
        }

        Normalize(settings);
    }

    public async Task SaveAsync(
        XBondClientWatchdogSettings settings,
        CancellationToken cancellationToken = default)
    {
        await _saveLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Normalize(settings);
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporaryPath = _filePath + ".tmp";
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(settings, JsonOptions),
                cancellationToken).ConfigureAwait(false);
            RestrictOwnerAccess(temporaryPath);
            File.Move(temporaryPath, _filePath, overwrite: true);
            RestrictOwnerAccess(_filePath);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    public static void Apply(XBondClientWatchdogSettings source, XBondClientWatchdogSettings target)
    {
        target.Enabled = source.Enabled;
        target.CheckIntervalSeconds = source.CheckIntervalSeconds;
        target.ConsecutiveUnhealthyChecks = source.ConsecutiveUnhealthyChecks;
        target.TunnelRttThresholdMs = source.TunnelRttThresholdMs;
        target.TunnelLossThresholdPercent = source.TunnelLossThresholdPercent;
        target.TunnelStaleAfterSeconds = source.TunnelStaleAfterSeconds;
        target.PhysicalProbeTarget = source.PhysicalProbeTarget;
        target.PhysicalMaxRttMs = source.PhysicalMaxRttMs;
        target.PhysicalMaxLossPercent = source.PhysicalMaxLossPercent;
        target.MinimumHealthyPhysicalPaths = source.MinimumHealthyPhysicalPaths;
        target.ProbeCount = source.ProbeCount;
        target.ProbeTimeoutSeconds = source.ProbeTimeoutSeconds;
        target.RestartCooldownMinutes = source.RestartCooldownMinutes;
        target.PostRestartGraceSeconds = source.PostRestartGraceSeconds;
        target.MaxRestartsPerHour = source.MaxRestartsPerHour;
        Normalize(target);
    }

    public static void Normalize(XBondClientWatchdogSettings settings)
    {
        settings.CheckIntervalSeconds = Math.Clamp(settings.CheckIntervalSeconds, 5, 300);
        settings.ConsecutiveUnhealthyChecks = Math.Clamp(settings.ConsecutiveUnhealthyChecks, 1, 12);
        settings.TunnelRttThresholdMs = Math.Clamp(settings.TunnelRttThresholdMs, 100, 5_000);
        settings.TunnelLossThresholdPercent = Math.Clamp(settings.TunnelLossThresholdPercent, 1, 100);
        settings.TunnelStaleAfterSeconds = Math.Clamp(settings.TunnelStaleAfterSeconds, 3, 120);
        settings.PhysicalProbeTarget = NormalizeHost(settings.PhysicalProbeTarget);
        settings.PhysicalMaxRttMs = Math.Clamp(settings.PhysicalMaxRttMs, 20, 5_000);
        settings.PhysicalMaxLossPercent = Math.Clamp(settings.PhysicalMaxLossPercent, 0, 99);
        settings.MinimumHealthyPhysicalPaths = Math.Clamp(settings.MinimumHealthyPhysicalPaths, 1, 8);
        settings.ProbeCount = Math.Clamp(settings.ProbeCount, 1, 5);
        settings.ProbeTimeoutSeconds = Math.Clamp(settings.ProbeTimeoutSeconds, 1, 10);
        settings.RestartCooldownMinutes = Math.Clamp(settings.RestartCooldownMinutes, 1, 120);
        settings.PostRestartGraceSeconds = Math.Clamp(settings.PostRestartGraceSeconds, 10, 300);
        settings.MaxRestartsPerHour = Math.Clamp(settings.MaxRestartsPerHour, 1, 12);
    }

    public static string NormalizeHost(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var target = value.Trim();
        if (Uri.TryCreate(target, UriKind.Absolute, out var uri))
        {
            return uri.Host;
        }

        if (target.StartsWith('['))
        {
            var end = target.IndexOf(']');
            return end > 0 ? target[1..end] : target;
        }

        var colon = target.LastIndexOf(':');
        return colon > 0 && target.Count(ch => ch == ':') == 1
            ? target[..colon]
            : target;
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

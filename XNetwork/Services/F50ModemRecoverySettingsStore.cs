using System.Text.Json;
using System.Text.Json.Serialization;
using XNetwork.Models;

namespace XNetwork.Services;

public sealed class F50ModemRecoverySettingsStore
{
    private const string SettingsFileName = "f50-modem-recovery-settings.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ILogger<F50ModemRecoverySettingsStore> _logger;
    private readonly string _filePath;
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public F50ModemRecoverySettingsStore(IHostEnvironment environment, ILogger<F50ModemRecoverySettingsStore> logger)
        : this(logger, Path.Combine(GetAppDataDirectory(environment), SettingsFileName))
    {
    }

    public F50ModemRecoverySettingsStore(ILogger<F50ModemRecoverySettingsStore> logger, string filePath)
    {
        _logger = logger;
        _filePath = filePath;
    }

    public void Load(F50ModemRecoverySettings settings)
    {
        if (!File.Exists(_filePath))
        {
            Normalize(settings);
            return;
        }

        try
        {
            var persisted = JsonSerializer.Deserialize<F50ModemRecoverySettings>(
                File.ReadAllText(_filePath),
                JsonOptions);
            if (persisted is null)
            {
                Normalize(settings);
                return;
            }

            Apply(persisted, settings);
            Normalize(settings);
            _logger.LogInformation("Loaded persisted F50 modem recovery settings from {Path}", _filePath);
        }
        catch (Exception ex)
        {
            Normalize(settings);
            _logger.LogWarning(ex, "Could not load F50 modem recovery settings from {Path}", _filePath);
        }
    }

    public async Task SaveAsync(F50ModemRecoverySettings settings, CancellationToken cancellationToken = default)
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
            _logger.LogInformation("Persisted F50 modem recovery settings to {Path}", _filePath);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    public static void Apply(F50ModemRecoverySettings source, F50ModemRecoverySettings target)
    {
        target.Enabled = source.Enabled;
        target.CheckIntervalMinutes = source.CheckIntervalMinutes;
        target.FailedRecoveryCooldownRounds = source.FailedRecoveryCooldownRounds;
        target.SettleTimeoutSeconds = source.SettleTimeoutSeconds;
        target.UsbResetEnabled = source.UsbResetEnabled;
        target.UsbResetCommandPath = source.UsbResetCommandPath;
        target.PingTarget = source.PingTarget;
        target.PingCount = source.PingCount;
        target.PingTimeoutSeconds = source.PingTimeoutSeconds;
        target.SevereLossPercent = source.SevereLossPercent;
        Normalize(target);
    }

    public static void Normalize(F50ModemRecoverySettings settings)
    {
        settings.CheckIntervalMinutes = Math.Clamp(settings.CheckIntervalMinutes, 1, 60);
        settings.FailedRecoveryCooldownRounds = Math.Clamp(settings.FailedRecoveryCooldownRounds, 0, 10);
        settings.SettleTimeoutSeconds = Math.Clamp(settings.SettleTimeoutSeconds, 15, 300);
        settings.UsbResetCommandPath = string.IsNullOrWhiteSpace(settings.UsbResetCommandPath)
            ? "usbreset"
            : settings.UsbResetCommandPath.Trim();
        settings.PingTarget = NormalizePingTarget(settings.PingTarget);
        settings.PingCount = Math.Clamp(settings.PingCount, 1, 5);
        settings.PingTimeoutSeconds = Math.Clamp(settings.PingTimeoutSeconds, 1, 10);
        settings.SevereLossPercent = Math.Clamp(settings.SevereLossPercent, 50, 100);
    }

    private static string NormalizePingTarget(string? value)
    {
        var target = string.IsNullOrWhiteSpace(value) ? "45.77.241.247" : value.Trim();
        var bracketIndex = target.IndexOf(']');
        if (bracketIndex >= 0)
        {
            return target[..(bracketIndex + 1)];
        }

        var colonIndex = target.IndexOf(':');
        return colonIndex > 0 && target.Count(ch => ch == ':') == 1
            ? target[..colonIndex]
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
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}

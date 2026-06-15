using System.Text.Json;
using System.Text.Json.Serialization;
using XNetwork.Models;

namespace XNetwork.Services;

public class XBondSettingsStore
{
    private const string SettingsFileName = "xbond-settings.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ILogger<XBondSettingsStore> _logger;
    private readonly string _filePath;
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public XBondSettingsStore(IHostEnvironment environment, ILogger<XBondSettingsStore> logger)
        : this(logger, Path.Combine(GetAppDataDirectory(environment), SettingsFileName))
    {
    }

    public XBondSettingsStore(ILogger<XBondSettingsStore> logger, string filePath)
    {
        _logger = logger;
        _filePath = filePath;
    }

    public void Load(XBondSettings settings)
    {
        if (!File.Exists(_filePath))
        {
            return;
        }

        try
        {
            var persistedSettings = JsonSerializer.Deserialize<XBondSettings>(File.ReadAllText(_filePath), JsonOptions);
            if (persistedSettings == null)
            {
                return;
            }

            Apply(persistedSettings, settings);
            _logger.LogInformation("Loaded persisted XBond settings from {Path}", _filePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load persisted XBond settings from {Path}", _filePath);
        }
    }

    public async Task SaveAsync(XBondSettings settings, CancellationToken cancellationToken = default)
    {
        await _saveLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            settings.TrafficEngineMode = XBondTrafficEngineModes.Normalize(settings.TrafficEngineMode);
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            var temporaryPath = _filePath + ".tmp";
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken).ConfigureAwait(false);
            RestrictOwnerAccess(temporaryPath);
            File.Move(temporaryPath, _filePath, overwrite: true);
            RestrictOwnerAccess(_filePath);
            _logger.LogInformation("Persisted XBond settings to {Path}", _filePath);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    private static void Apply(XBondSettings source, XBondSettings target)
    {
        target.Enabled = source.Enabled;
        target.TrafficEngineMode = XBondTrafficEngineModes.Normalize(source.TrafficEngineMode);
        target.AllowServiceControl = source.AllowServiceControl;
        target.ClientServiceName = string.IsNullOrWhiteSpace(source.ClientServiceName)
            ? target.ClientServiceName
            : source.ClientServiceName;
        target.ServiceManagerPath = string.IsNullOrWhiteSpace(source.ServiceManagerPath)
            ? target.ServiceManagerPath
            : source.ServiceManagerPath;
        target.UseSudoForServiceManager = source.UseSudoForServiceManager;
        target.SudoPath = string.IsNullOrWhiteSpace(source.SudoPath)
            ? target.SudoPath
            : source.SudoPath;
        target.ServiceCommandTimeoutSeconds = source.ServiceCommandTimeoutSeconds <= 0
            ? target.ServiceCommandTimeoutSeconds
            : source.ServiceCommandTimeoutSeconds;
        target.ClientBinaryPath = string.IsNullOrWhiteSpace(source.ClientBinaryPath)
            ? target.ClientBinaryPath
            : source.ClientBinaryPath;
        target.ClientConfigPath = string.IsNullOrWhiteSpace(source.ClientConfigPath)
            ? target.ClientConfigPath
            : source.ClientConfigPath;
        target.RuntimeStatusPath = string.IsNullOrWhiteSpace(source.RuntimeStatusPath)
            ? target.RuntimeStatusPath
            : source.RuntimeStatusPath;
        target.ScheduleMode = string.IsNullOrWhiteSpace(source.ScheduleMode)
            ? target.ScheduleMode
            : source.ScheduleMode;
        target.MaxActiveBackups = source.MaxActiveBackups <= 0 ? target.MaxActiveBackups : source.MaxActiveBackups;
        target.StatusTimeoutSeconds = source.StatusTimeoutSeconds <= 0
            ? target.StatusTimeoutSeconds
            : source.StatusTimeoutSeconds;
        target.PublicTestServerAddress = string.IsNullOrWhiteSpace(source.PublicTestServerAddress)
            ? target.PublicTestServerAddress
            : source.PublicTestServerAddress;
        target.PublicTestPathId = source.PublicTestPathId <= 0 ? target.PublicTestPathId : source.PublicTestPathId;
        target.PublicTestPathIds = source.PublicTestPathIds ?? new List<int>();
        target.PublicTestBinds = source.PublicTestBinds ?? new List<string>();
        target.PublicTestCount = source.PublicTestCount <= 0 ? target.PublicTestCount : source.PublicTestCount;
        target.PublicTestIntervalMs = source.PublicTestIntervalMs <= 0 ? target.PublicTestIntervalMs : source.PublicTestIntervalMs;
        target.PublicTestPacketTimeoutMs = source.PublicTestPacketTimeoutMs <= 0
            ? target.PublicTestPacketTimeoutMs
            : source.PublicTestPacketTimeoutMs;
        target.PublicTestCommandTimeoutSeconds = source.PublicTestCommandTimeoutSeconds <= 0
            ? target.PublicTestCommandTimeoutSeconds
            : source.PublicTestCommandTimeoutSeconds;
        target.PublicTestKeyEnvironmentVariable = string.IsNullOrWhiteSpace(source.PublicTestKeyEnvironmentVariable)
            ? target.PublicTestKeyEnvironmentVariable
            : source.PublicTestKeyEnvironmentVariable;
        target.PublicTestKeyFilePath = string.IsNullOrWhiteSpace(source.PublicTestKeyFilePath)
            ? target.PublicTestKeyFilePath
            : source.PublicTestKeyFilePath;
        target.TunnelDevice = string.IsNullOrWhiteSpace(source.TunnelDevice)
            ? target.TunnelDevice
            : source.TunnelDevice;
        target.TunnelSource = string.IsNullOrWhiteSpace(source.TunnelSource)
            ? target.TunnelSource
            : source.TunnelSource;
        target.RouteCommandPath = string.IsNullOrWhiteSpace(source.RouteCommandPath)
            ? target.RouteCommandPath
            : source.RouteCommandPath;
        target.PingCommandPath = string.IsNullOrWhiteSpace(source.PingCommandPath)
            ? target.PingCommandPath
            : source.PingCommandPath;
        target.ScopedRouteDefaultTarget = string.IsNullOrWhiteSpace(source.ScopedRouteDefaultTarget)
            ? target.ScopedRouteDefaultTarget
            : source.ScopedRouteDefaultTarget;
        target.ScopedRouteTestCount = source.ScopedRouteTestCount <= 0
            ? target.ScopedRouteTestCount
            : source.ScopedRouteTestCount;
        target.ScopedRoutePacketTimeoutSeconds = source.ScopedRoutePacketTimeoutSeconds <= 0
            ? target.ScopedRoutePacketTimeoutSeconds
            : source.ScopedRoutePacketTimeoutSeconds;
        target.ScopedRouteCommandTimeoutSeconds = source.ScopedRouteCommandTimeoutSeconds <= 0
            ? target.ScopedRouteCommandTimeoutSeconds
            : source.ScopedRouteCommandTimeoutSeconds;
        target.SpeedTestCommandPath = string.IsNullOrWhiteSpace(source.SpeedTestCommandPath)
            ? target.SpeedTestCommandPath
            : source.SpeedTestCommandPath;
        target.SpeedTestCommandTimeoutSeconds = source.SpeedTestCommandTimeoutSeconds <= 0
            ? target.SpeedTestCommandTimeoutSeconds
            : source.SpeedTestCommandTimeoutSeconds;
        target.SpeedTestHttpTimeoutSeconds = source.SpeedTestHttpTimeoutSeconds <= 0
            ? target.SpeedTestHttpTimeoutSeconds
            : source.SpeedTestHttpTimeoutSeconds;
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

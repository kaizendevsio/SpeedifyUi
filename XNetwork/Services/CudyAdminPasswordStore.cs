using System.Text.Json;
using System.Text.Json.Serialization;
using XNetwork.Models;

namespace XNetwork.Services;

public class CudyAdminPasswordStore(IHostEnvironment environment, ILogger<CudyAdminPasswordStore> logger)
{
    private const string FileName = "cudy-admin-password";
    private const string SettingsFileName = "cudy-ap-automation-settings.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public void Load(CudyApAutomationSettings settings)
    {
        LoadSettings(settings);
        LoadPassword(settings);
    }

    public void LoadPassword(CudyApAutomationSettings settings)
    {
        if (!string.IsNullOrEmpty(settings.AdminPassword))
        {
            return;
        }

        var path = GetPasswordFilePath(settings);
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            var password = File.ReadAllText(path).TrimEnd('\r', '\n');
            if (!string.IsNullOrEmpty(password))
            {
                settings.AdminPassword = password;
                logger.LogInformation("Loaded persisted Cudy admin password from {Path}", path);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not load persisted Cudy admin password from {Path}", path);
        }
    }

    public void LoadSettings(CudyApAutomationSettings settings)
    {
        var path = GetSettingsFilePath();
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            var persistedSettings = JsonSerializer.Deserialize<PersistedCudyApSettings>(File.ReadAllText(path), JsonOptions);
            if (persistedSettings == null)
            {
                return;
            }

            persistedSettings.ApplyTo(settings);
            logger.LogInformation("Loaded persisted Cudy AP automation settings from {Path}", path);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not load persisted Cudy AP automation settings from {Path}", path);
        }
    }

    public async Task SaveAsync(CudyApAutomationSettings settings, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(settings.AdminPassword))
        {
            return;
        }

        var path = GetPasswordFilePath(settings);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = path + ".tmp";
        await File.WriteAllTextAsync(temporaryPath, settings.AdminPassword, cancellationToken).ConfigureAwait(false);
        RestrictOwnerAccess(temporaryPath);
        File.Move(temporaryPath, path, overwrite: true);
        RestrictOwnerAccess(path);
        logger.LogInformation("Persisted Cudy admin password to {Path}", path);
    }

    public async Task SaveSettingsAsync(CudyApAutomationSettings settings, CancellationToken cancellationToken = default)
    {
        var path = GetSettingsFilePath();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(PersistedCudyApSettings.From(settings), JsonOptions);
        var temporaryPath = path + ".tmp";
        await File.WriteAllTextAsync(temporaryPath, json, cancellationToken).ConfigureAwait(false);
        RestrictOwnerAccess(temporaryPath);
        File.Move(temporaryPath, path, overwrite: true);
        RestrictOwnerAccess(path);
        logger.LogInformation("Persisted Cudy AP automation settings to {Path}", path);
    }

    public string GetPasswordFilePath(CudyApAutomationSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.AdminPasswordFilePath))
        {
            return ExpandHomePath(settings.AdminPasswordFilePath.Trim());
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
        {
            appData = Path.Combine(environment.ContentRootPath, "App_Data");
        }

        return Path.Combine(appData, "XNetwork", FileName);
    }

    public string GetSettingsFilePath()
    {
        return Path.Combine(GetAppDataDirectory(), SettingsFileName);
    }

    private string GetAppDataDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
        {
            appData = Path.Combine(environment.ContentRootPath, "App_Data");
        }

        return Path.Combine(appData, "XNetwork");
    }

    private static string ExpandHomePath(string path)
    {
        if (path == "~")
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return string.IsNullOrWhiteSpace(home) ? path : Path.Combine(home, path[2..]);
        }

        return path;
    }

    private static void RestrictOwnerAccess(string path)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private sealed class PersistedCudyApSettings
    {
        public bool Enabled { get; set; }

        public string ManagementBaseUrl { get; set; } = "";

        public string AdminPasswordFilePath { get; set; } = "";

        public string AdminPasswordEnvironmentVariable { get; set; } = "";

        public string WifiInterface { get; set; } = "wlan0";

        public string HomeSsid { get; set; } = "";

        public string HomeBssid { get; set; } = "";

        public int DisableWhenSignalAtLeast { get; set; }

        public int EnableWhenSignalBelow { get; set; }

        public int CheckIntervalSeconds { get; set; }

        public int DisableAfterSeenSeconds { get; set; }

        public int EnableAfterMissingSeconds { get; set; }

        public bool ReEnableWhenHomeMissing { get; set; }

        public bool Disable2G { get; set; }

        public bool Disable5G { get; set; }

        public int RequestTimeoutSeconds { get; set; }

        public static PersistedCudyApSettings From(CudyApAutomationSettings settings)
        {
            return new PersistedCudyApSettings
            {
                Enabled = settings.Enabled,
                ManagementBaseUrl = settings.ManagementBaseUrl,
                AdminPasswordFilePath = settings.AdminPasswordFilePath,
                AdminPasswordEnvironmentVariable = settings.AdminPasswordEnvironmentVariable,
                WifiInterface = settings.WifiInterface,
                HomeSsid = settings.HomeSsid,
                HomeBssid = settings.HomeBssid,
                DisableWhenSignalAtLeast = settings.DisableWhenSignalAtLeast,
                EnableWhenSignalBelow = settings.EnableWhenSignalBelow,
                CheckIntervalSeconds = settings.CheckIntervalSeconds,
                DisableAfterSeenSeconds = settings.DisableAfterSeenSeconds,
                EnableAfterMissingSeconds = settings.EnableAfterMissingSeconds,
                ReEnableWhenHomeMissing = settings.ReEnableWhenHomeMissing,
                Disable2G = settings.Disable2G,
                Disable5G = settings.Disable5G,
                RequestTimeoutSeconds = settings.RequestTimeoutSeconds
            };
        }

        public void ApplyTo(CudyApAutomationSettings settings)
        {
            settings.Enabled = Enabled;
            settings.ManagementBaseUrl = ManagementBaseUrl ?? "";
            settings.AdminPasswordFilePath = AdminPasswordFilePath ?? "";
            settings.AdminPasswordEnvironmentVariable = AdminPasswordEnvironmentVariable ?? "";
            settings.WifiInterface = string.IsNullOrWhiteSpace(WifiInterface) ? "wlan0" : WifiInterface;
            settings.HomeSsid = HomeSsid ?? "";
            settings.HomeBssid = HomeBssid ?? "";
            settings.DisableWhenSignalAtLeast = DisableWhenSignalAtLeast;
            settings.EnableWhenSignalBelow = EnableWhenSignalBelow;
            settings.CheckIntervalSeconds = CheckIntervalSeconds;
            settings.DisableAfterSeenSeconds = DisableAfterSeenSeconds;
            settings.EnableAfterMissingSeconds = EnableAfterMissingSeconds;
            settings.ReEnableWhenHomeMissing = ReEnableWhenHomeMissing;
            settings.Disable2G = Disable2G;
            settings.Disable5G = Disable5G;
            settings.RequestTimeoutSeconds = RequestTimeoutSeconds;
        }
    }
}

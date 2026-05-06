using XNetwork.Models;

namespace XNetwork.Services;

public class CudyAdminPasswordStore(IHostEnvironment environment, ILogger<CudyAdminPasswordStore> logger)
{
    private const string FileName = "cudy-admin-password";

    public void Load(CudyApAutomationSettings settings)
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
}

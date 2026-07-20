using System.Text.Json;
using System.Text.Json.Serialization;
using XNetwork.Models;

namespace XNetwork.Services;

public interface IXBondClientWatchdogStateStore
{
    XBondClientWatchdogStateLoadResult Load(DateTimeOffset nowUtc);

    Task SaveAsync(
        XBondClientWatchdogState state,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);
}

public sealed class XBondClientWatchdogStateStore : IXBondClientWatchdogStateStore
{
    private const string StateFileName = "xbond-client-watchdog-state.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ILogger<XBondClientWatchdogStateStore> _logger;
    private readonly string _filePath;
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public XBondClientWatchdogStateStore(
        IHostEnvironment environment,
        ILogger<XBondClientWatchdogStateStore> logger)
        : this(logger, GetDefaultStateFilePath())
    {
    }

    public XBondClientWatchdogStateStore(
        ILogger<XBondClientWatchdogStateStore> logger,
        string filePath)
    {
        _logger = logger;
        _filePath = filePath;
    }

    public XBondClientWatchdogStateLoadResult Load(DateTimeOffset nowUtc)
    {
        if (!File.Exists(_filePath))
        {
            if (Directory.Exists(_filePath))
            {
                return LoadFailure($"Watchdog state path is a directory and cannot be read: {_filePath}");
            }

            return new XBondClientWatchdogStateLoadResult(
                new XBondClientWatchdogState(),
                CanRestartAutomatically: true,
                FailureReason: null);
        }

        try
        {
            var state = JsonSerializer.Deserialize<XBondClientWatchdogState>(
                File.ReadAllText(_filePath),
                JsonOptions);
            if (state is not null)
            {
                Normalize(state, nowUtc);
                _logger.LogInformation("Loaded persisted XBond client watchdog state from {Path}", _filePath);
                return new XBondClientWatchdogStateLoadResult(
                    state,
                    CanRestartAutomatically: !state.AutomaticRestartsBlocked,
                    state.AutomaticRestartsBlocked
                        ? "The watchdog restart state contains an unfinished persistence transaction."
                        : null);
            }

            return LoadFailure($"Watchdog state is empty or invalid: {_filePath}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load XBond client watchdog state from {Path}", _filePath);
            return LoadFailure($"Watchdog state could not be read: {ex.Message}");
        }
    }

    public async Task SaveAsync(
        XBondClientWatchdogState state,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        await _saveLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Normalize(state, nowUtc);
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporaryPath = _filePath + ".tmp";
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(state, JsonOptions),
                cancellationToken).ConfigureAwait(false);
            RestrictOwnerAccess(temporaryPath);
            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    public static void Normalize(XBondClientWatchdogState state, DateTimeOffset nowUtc)
    {
        var cutoff = nowUtc - TimeSpan.FromHours(1);
        state.RestartHistoryUtc = (state.RestartHistoryUtc ?? [])
            .Where(restart => restart > cutoff && restart <= nowUtc)
            .OrderBy(restart => restart)
            .ToList();

        if (state.SuppressedUntilUtc <= nowUtc)
        {
            state.SuppressedUntilUtc = null;
        }
    }

    public static string ResolveStateFilePath(
        string? applicationData,
        string? localApplicationData,
        string? userProfile,
        string? commonApplicationData)
    {
        string directory;
        if (!string.IsNullOrWhiteSpace(applicationData))
        {
            directory = Path.Combine(applicationData, "XNetwork");
        }
        else if (!string.IsNullOrWhiteSpace(localApplicationData))
        {
            directory = Path.Combine(localApplicationData, "XNetwork");
        }
        else if (!string.IsNullOrWhiteSpace(userProfile))
        {
            directory = Path.Combine(userProfile, ".local", "state", "xnetwork");
        }
        else
        {
            directory = Path.Combine(
                string.IsNullOrWhiteSpace(commonApplicationData)
                    ? GetSystemDataDirectory()
                    : commonApplicationData,
                OperatingSystem.IsWindows() ? "XNetwork" : "xnetwork");
        }

        return Path.Combine(directory, StateFileName);
    }

    private static string GetDefaultStateFilePath() =>
        ResolveStateFilePath(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));

    private static string GetSystemDataDirectory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return "/var/lib";
        }

        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory);
        return Path.Combine(string.IsNullOrWhiteSpace(systemRoot) ? @"C:\" : systemRoot, "ProgramData");
    }

    private XBondClientWatchdogStateLoadResult LoadFailure(string reason)
    {
        _logger.LogError(
            "{Reason} Automatic XBond client watchdog restarts are disabled until the state is repaired.",
            reason);
        return new XBondClientWatchdogStateLoadResult(
            new XBondClientWatchdogState { AutomaticRestartsBlocked = true },
            CanRestartAutomatically: false,
            FailureReason: reason);
    }

    private static void RestrictOwnerAccess(string path)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}

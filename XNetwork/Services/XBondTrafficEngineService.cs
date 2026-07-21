using System.Diagnostics;
using XNetwork.Models;

namespace XNetwork.Services;

public class XBondTrafficEngineService
{
    private static readonly TimeSpan DefaultStatusCacheDuration = TimeSpan.FromSeconds(3);

    private readonly ILogger<XBondTrafficEngineService> _logger;
    private readonly XBondSettings _settings;
    private readonly XBondSettingsStore _settingsStore;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly SemaphoreSlim _statusLock = new(1, 1);
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _statusCacheDuration;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<ServiceCommandResult>> _serviceManager;
    private readonly Func<bool> _isLinux;
    private XBondTrafficEngineStatus? _cachedStatus;
    private DateTimeOffset _statusExpiresAtUtc = DateTimeOffset.MinValue;

    public XBondTrafficEngineService(
        ILogger<XBondTrafficEngineService> logger,
        XBondSettings settings,
        XBondSettingsStore settingsStore)
        : this(logger, settings, settingsStore, TimeProvider.System, null, null, null)
    {
    }

    public XBondTrafficEngineService(
        ILogger<XBondTrafficEngineService> logger,
        XBondSettings settings,
        XBondSettingsStore settingsStore,
        TimeProvider? timeProvider,
        Func<IReadOnlyList<string>, CancellationToken, Task<ServiceCommandResult>>? serviceManager,
        Func<bool>? isLinux,
        TimeSpan? statusCacheDuration)
    {
        _logger = logger;
        _settings = settings;
        _settingsStore = settingsStore;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _statusCacheDuration = statusCacheDuration ?? DefaultStatusCacheDuration;
        _serviceManager = serviceManager ?? RunServiceManagerAsync;
        _isLinux = isLinux ?? OperatingSystem.IsLinux;
    }

    public async Task<XBondTrafficEngineStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        if (_cachedStatus is not null && now < _statusExpiresAtUtc)
        {
            return _cachedStatus;
        }

        await _statusLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = _timeProvider.GetUtcNow();
            if (_cachedStatus is not null && now < _statusExpiresAtUtc)
            {
                return _cachedStatus;
            }

            _cachedStatus = await RefreshStatusAsync(cancellationToken).ConfigureAwait(false);
            _statusExpiresAtUtc = now + _statusCacheDuration;
            return _cachedStatus;
        }
        finally
        {
            _statusLock.Release();
        }
    }

    private async Task<XBondTrafficEngineStatus> RefreshStatusAsync(CancellationToken cancellationToken)
    {
        var status = BaseStatus();
        if (!_isLinux())
        {
            status.ClientServiceState = "unsupported";
            status.Message = "uLink service control is only available on Linux.";
            return status;
        }

        var serviceState = await _serviceManager(
            ["is-active", _settings.ClientServiceName],
            cancellationToken).ConfigureAwait(false);

        status.ClientServiceState = serviceState.ExitCode == 0
            ? "active"
            : NormalizeServiceState(serviceState.Output);
        status.ClientServiceRunning = status.ClientServiceState == "active";

        var enableState = await _serviceManager(
            ["is-enabled", _settings.ClientServiceName],
            cancellationToken).ConfigureAwait(false);
        status.ClientServiceEnableState = enableState.ExitCode == 0
            ? "enabled"
            : NormalizeServiceState(enableState.Output);
        status.ClientServiceEnabled = status.ClientServiceEnableState == "enabled";

        status.Message = BuildStatusMessage(status);
        return status;
    }

    public async Task<XBondTrafficEngineStatus> SetModeAsync(string mode, CancellationToken cancellationToken = default)
    {
        _settings.TrafficEngineMode = XBondTrafficEngineModes.Normalize(mode);
        _settings.Enabled = true;
        await _settingsStore.SaveAsync(_settings, cancellationToken).ConfigureAwait(false);
        InvalidateStatusCache();
        return await GetStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<XBondTrafficEngineStatus> StartAsync(CancellationToken cancellationToken = default)
    {
        return RunServiceActionAsync("start", cancellationToken);
    }

    public Task<XBondTrafficEngineStatus> StopAsync(CancellationToken cancellationToken = default)
    {
        return RunServiceActionAsync("stop", cancellationToken);
    }

    public Task<XBondTrafficEngineStatus> RestartAsync(CancellationToken cancellationToken = default)
    {
        return RunServiceActionAsync("restart", cancellationToken);
    }

    public Task<XBondTrafficEngineStatus> EnableAtBootAsync(CancellationToken cancellationToken = default)
    {
        return RunServiceActionAsync("enable", cancellationToken);
    }

    public Task<XBondTrafficEngineStatus> DisableAtBootAsync(CancellationToken cancellationToken = default)
    {
        return RunServiceActionAsync("disable", cancellationToken);
    }

    private async Task<XBondTrafficEngineStatus> RunServiceActionAsync(string action, CancellationToken cancellationToken)
    {
        if (!_settings.AllowServiceControl)
        {
            return ErrorStatus("uLink service control is disabled in configuration.");
        }

        if (!_isLinux())
        {
            return ErrorStatus("uLink service control is only available on Linux.");
        }

        if (!await _operationLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return ErrorStatus("Another uLink service operation is already running.");
        }

        try
        {
            InvalidateStatusCache();
            var result = await _serviceManager(
                [action, _settings.ClientServiceName],
                cancellationToken).ConfigureAwait(false);
            InvalidateStatusCache();
            var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                status.Error = string.IsNullOrWhiteSpace(result.Output)
                    ? $"{ServiceCommandLabel(action)} exited with code {result.ExitCode}."
                    : result.Output;
                status.Message = $"uLink service {action} failed.";
            }

            return status;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "uLink service {Action} failed", action);
            return ErrorStatus(ex.Message);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private XBondTrafficEngineStatus BaseStatus()
    {
        return new XBondTrafficEngineStatus
        {
            Mode = XBondTrafficEngineModes.Normalize(_settings.TrafficEngineMode),
            ServiceControlAllowed = _settings.AllowServiceControl,
            ClientServiceName = _settings.ClientServiceName,
            UpdatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime
        };
    }

    private XBondTrafficEngineStatus ErrorStatus(string error)
    {
        var status = BaseStatus();
        status.Error = error;
        status.Message = error;
        return status;
    }

    private async Task<ServiceCommandResult> RunServiceManagerAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_settings.ServiceCommandTimeoutSeconds, 3, 60)));

        var startInfo = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (_settings.UseSudoForServiceManager && _isLinux())
        {
            startInfo.FileName = _settings.SudoPath;
            startInfo.ArgumentList.Add("-n");
            startInfo.ArgumentList.Add(_settings.ServiceManagerPath);
        }
        else
        {
            startInfo.FileName = _settings.ServiceManagerPath;
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start {_settings.ServiceManagerPath}.");
        }

        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);

            var output = string.Join('\n', new[]
                {
                    await stdoutTask.ConfigureAwait(false),
                    await stderrTask.ConfigureAwait(false)
                }
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Select(text => text.Trim()));

            return new ServiceCommandResult(process.ExitCode, output);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException($"{ServiceCommandLabel(arguments.FirstOrDefault() ?? "")} timed out after {_settings.ServiceCommandTimeoutSeconds} seconds.");
        }
        catch
        {
            TryKill(process);
            throw;
        }
    }

    private static string NormalizeServiceState(string output)
    {
        var firstLine = output
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();

        return string.IsNullOrWhiteSpace(firstLine) ? "inactive" : firstLine.ToLowerInvariant();
    }

    private static string BuildStatusMessage(XBondTrafficEngineStatus status)
    {
        if (!status.ServiceControlAllowed)
        {
            return "uLink service control is locked by configuration.";
        }

        return status.ClientServiceRunning
            ? "uLink tunnel service is running."
            : "uLink tunnel service is stopped.";
    }

    private string ServiceCommandLabel(string action)
    {
        return _settings.UseSudoForServiceManager && _isLinux()
            ? $"{_settings.SudoPath} -n {_settings.ServiceManagerPath} {action}"
            : $"{_settings.ServiceManagerPath} {action}";
    }

    private void InvalidateStatusCache()
    {
        _statusExpiresAtUtc = DateTimeOffset.MinValue;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort cleanup for a failed service-manager command.
        }
    }

    public sealed record ServiceCommandResult(int ExitCode, string Output);
}

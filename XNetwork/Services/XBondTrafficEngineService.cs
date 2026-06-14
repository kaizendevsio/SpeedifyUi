using System.Diagnostics;
using XNetwork.Models;

namespace XNetwork.Services;

public class XBondTrafficEngineService(
    ILogger<XBondTrafficEngineService> logger,
    XBondSettings settings,
    XBondSettingsStore settingsStore)
{
    private readonly SemaphoreSlim _operationLock = new(1, 1);

    public async Task<XBondTrafficEngineStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var status = BaseStatus();
        if (!OperatingSystem.IsLinux())
        {
            status.ClientServiceState = "unsupported";
            status.Message = "XBond service control is only available on Linux.";
            return status;
        }

        var serviceState = await RunServiceManagerAsync(
            ["is-active", settings.ClientServiceName],
            cancellationToken).ConfigureAwait(false);

        status.ClientServiceState = serviceState.ExitCode == 0
            ? "active"
            : NormalizeServiceState(serviceState.Output);
        status.ClientServiceRunning = status.ClientServiceState == "active";

        var enableState = await RunServiceManagerAsync(
            ["is-enabled", settings.ClientServiceName],
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
        var normalizedMode = XBondTrafficEngineModes.Normalize(mode);
        if (normalizedMode == XBondTrafficEngineModes.XBondPrimary && !settings.AllowPrimaryMode)
        {
            return ErrorStatus("XBond primary mode is locked. Enable AllowPrimaryMode in configuration before selecting it.");
        }

        settings.TrafficEngineMode = normalizedMode;
        settings.Enabled = normalizedMode != XBondTrafficEngineModes.SpeedifyPrimary;
        await settingsStore.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
        return await GetStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<XBondTrafficEngineStatus> StartCanaryAsync(CancellationToken cancellationToken = default)
    {
        return RunServiceActionAsync("start", cancellationToken);
    }

    public Task<XBondTrafficEngineStatus> StopCanaryAsync(CancellationToken cancellationToken = default)
    {
        return RunServiceActionAsync("stop", cancellationToken);
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
        if (!settings.AllowServiceControl)
        {
            return ErrorStatus("XBond service control is disabled in configuration.");
        }

        if (!OperatingSystem.IsLinux())
        {
            return ErrorStatus("XBond service control is only available on Linux.");
        }

        if (settings.TrafficEngineMode == XBondTrafficEngineModes.XBondPrimary && !settings.AllowPrimaryMode)
        {
            return ErrorStatus("XBond primary mode is locked. Enable AllowPrimaryMode in configuration before service control.");
        }

        if (!await _operationLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return ErrorStatus("Another XBond service operation is already running.");
        }

        try
        {
            var result = await RunServiceManagerAsync(
                [action, settings.ClientServiceName],
                cancellationToken).ConfigureAwait(false);
            var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                status.Error = string.IsNullOrWhiteSpace(result.Output)
                    ? $"{ServiceCommandLabel(action)} exited with code {result.ExitCode}."
                    : result.Output;
                status.Message = $"XBond service {action} failed.";
            }

            return status;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "XBond service {Action} failed", action);
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
            Mode = XBondTrafficEngineModes.Normalize(settings.TrafficEngineMode),
            ServiceControlAllowed = settings.AllowServiceControl,
            PrimaryModeAllowed = settings.AllowPrimaryMode,
            ClientServiceName = settings.ClientServiceName,
            UpdatedAtUtc = DateTime.UtcNow
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
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.ServiceCommandTimeoutSeconds, 3, 60)));

        var startInfo = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (settings.UseSudoForServiceManager && OperatingSystem.IsLinux())
        {
            startInfo.FileName = settings.SudoPath;
            startInfo.ArgumentList.Add("-n");
            startInfo.ArgumentList.Add(settings.ServiceManagerPath);
        }
        else
        {
            startInfo.FileName = settings.ServiceManagerPath;
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start {settings.ServiceManagerPath}.");
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
            throw new TimeoutException($"{ServiceCommandLabel(arguments.FirstOrDefault() ?? "")} timed out after {settings.ServiceCommandTimeoutSeconds} seconds.");
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
            return "Speedify remains primary. XBond service control is locked by configuration.";
        }

        return status.Mode switch
        {
            XBondTrafficEngineModes.XBondCanary => status.ClientServiceRunning
                ? "XBond canary service is running."
                : "XBond canary is selected but the client service is stopped.",
            XBondTrafficEngineModes.XBondPrimary => status.ClientServiceRunning
                ? "XBond primary mode is selected and the client service is running."
                : "XBond primary mode is selected but the client service is stopped.",
            _ => status.ClientServiceRunning
                ? "Speedify remains primary; XBond canary service is running."
                : "Speedify primary is selected."
        };
    }

    private string ServiceCommandLabel(string action)
    {
        return settings.UseSudoForServiceManager && OperatingSystem.IsLinux()
            ? $"{settings.SudoPath} -n {settings.ServiceManagerPath} {action}"
            : $"{settings.ServiceManagerPath} {action}";
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

    private sealed record ServiceCommandResult(int ExitCode, string Output);
}

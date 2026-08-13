using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using XNetwork.Models;

namespace XNetwork.Services;

public sealed class StarlinkPathBindingService(
    ILogger<StarlinkPathBindingService> logger,
    StarlinkTelemetrySettings starlinkSettings,
    XBondSettings xbondSettings,
    IStarlinkInterfaceResolver interfaceResolver,
    XBondClientConfigService configService) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!starlinkSettings.Enabled || !starlinkSettings.AdapterProbeEnabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Unable to reconcile the detected Starlink adapter with its uLink path");
            }

            await Task.Delay(starlinkSettings.AdapterProbeInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    internal async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        var resolution = await interfaceResolver.ResolveAsync(cancellationToken).ConfigureAwait(false);
        if (!resolution.IsAvailable || string.IsNullOrWhiteSpace(resolution.InterfaceName))
        {
            logger.LogDebug("Starlink adapter reconciliation skipped: {Reason}", resolution.Reason);
            return;
        }

        var config = await configService.ReadCurrentConfigAsync(cancellationToken).ConfigureAwait(false);
        var plan = CreatePlan(config, resolution, starlinkSettings.AdapterNameHints);
        if (!plan.Required)
        {
            logger.LogDebug("Starlink adapter reconciliation skipped: {Reason}", plan.Reason);
            return;
        }

        var command = await RebindPathAsync(plan.PathId, plan.DetectedInterface!, cancellationToken)
            .ConfigureAwait(false);
        if (!command.Success)
        {
            logger.LogWarning(
                "Could not hot-rebind Starlink path {PathId} from {CurrentInterface} to {DetectedInterface}: {Error}",
                plan.PathId,
                plan.CurrentInterface,
                plan.DetectedInterface,
                command.Message);
            return;
        }

        var persisted = await configService.PersistPathInterfaceAsync(
            plan.PathId,
            plan.DetectedInterface!,
            cancellationToken).ConfigureAwait(false);
        if (!persisted.Success)
        {
            logger.LogError(
                "Starlink path {PathId} was hot-rebound to {DetectedInterface}, but persistence failed: {Error}",
                plan.PathId,
                plan.DetectedInterface,
                persisted.Message);
            return;
        }

        logger.LogInformation(
            "Hot-rebound Starlink path {PathId} from {CurrentInterface} to {DetectedInterface} without restarting uLink",
            plan.PathId,
            plan.CurrentInterface,
            plan.DetectedInterface);
    }

    public static StarlinkPathBindingPlan CreatePlan(
        XBondClientConfig config,
        StarlinkInterfaceResolution resolution,
        IReadOnlyList<string> adapterNameHints)
    {
        if (!resolution.IsAvailable || string.IsNullOrWhiteSpace(resolution.InterfaceName))
        {
            return StarlinkPathBindingPlan.Skip(resolution.Reason);
        }

        var path = config.Paths.FirstOrDefault(candidate =>
            candidate.Enabled && adapterNameHints.Any(hint =>
                !string.IsNullOrWhiteSpace(hint) &&
                candidate.Name.Contains(hint, StringComparison.OrdinalIgnoreCase)));
        if (path is null)
        {
            return StarlinkPathBindingPlan.Skip("No enabled Starlink uLink path matched the configured name hints.");
        }

        if (string.Equals(path.InterfaceName, resolution.InterfaceName, StringComparison.OrdinalIgnoreCase))
        {
            return StarlinkPathBindingPlan.Skip("The Starlink path already uses the verified adapter.");
        }

        var conflict = config.Paths.FirstOrDefault(candidate =>
            candidate.Enabled && candidate.Id != path.Id &&
            string.Equals(candidate.InterfaceName, resolution.InterfaceName, StringComparison.OrdinalIgnoreCase));
        if (conflict is not null)
        {
            return StarlinkPathBindingPlan.Skip(
                $"Verified Starlink adapter {resolution.InterfaceName} is already assigned to uLink path {conflict.Id}.");
        }

        return new(true, path.Id, path.InterfaceName, resolution.InterfaceName, resolution.Reason);
    }

    private async Task<StarlinkPathRebindResult> RebindPathAsync(
        int pathId,
        string interfaceName,
        CancellationToken cancellationToken)
    {
        var binary = ResolveClientBinaryPath();
        var arguments = new List<string>();
        var fileName = binary;
        if (xbondSettings.UseSudoForServiceManager && OperatingSystem.IsLinux())
        {
            fileName = string.IsNullOrWhiteSpace(xbondSettings.SudoPath) ? "sudo" : xbondSettings.SudoPath;
            arguments.Add("-n");
            arguments.Add(binary);
        }

        arguments.AddRange([
            "path-rebind",
            "--socket",
            string.IsNullOrWhiteSpace(xbondSettings.ClientControlSocketPath)
                ? "/run/xbond/client-control.sock"
                : xbondSettings.ClientControlSocketPath,
            "--json",
            "--path-id",
            pathId.ToString(CultureInfo.InvariantCulture),
            "--interface",
            interfaceName
        ]);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(xbondSettings.ServiceCommandTimeoutSeconds, 3, 60)));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                return new(false, string.IsNullOrWhiteSpace(error) ? output.Trim() : error.Trim());
            }

            using var json = JsonDocument.Parse(output);
            var ok = json.RootElement.TryGetProperty("ok", out var okElement) && okElement.GetBoolean();
            var message = json.RootElement.TryGetProperty("message", out var messageElement)
                ? messageElement.GetString() ?? ""
                : "";
            return new(ok, message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new(false, ex.Message);
        }
    }

    private string ResolveClientBinaryPath()
    {
        if (Path.IsPathRooted(xbondSettings.ClientBinaryPath))
        {
            return xbondSettings.ClientBinaryPath;
        }

        return OperatingSystem.IsLinux()
            ? $"/usr/local/bin/{xbondSettings.ClientBinaryPath}"
            : xbondSettings.ClientBinaryPath;
    }
}

public sealed record StarlinkPathBindingPlan(
    bool Required,
    int PathId,
    string? CurrentInterface,
    string? DetectedInterface,
    string Reason)
{
    public static StarlinkPathBindingPlan Skip(string reason) => new(false, 0, null, null, reason);
}

internal sealed record StarlinkPathRebindResult(bool Success, string Message);

using System.Diagnostics;
using XNetwork.Models;

namespace XNetwork.Services;

public sealed record StarlinkLanAccessCommandResult(bool Succeeded, string Message);

public interface IStarlinkLanAccessCommandRunner
{
    Task<StarlinkLanAccessCommandResult> ApplyAsync(
        string starlinkInterface,
        StarlinkLanAccessSettings settings,
        CancellationToken cancellationToken);

    Task<StarlinkLanAccessCommandResult> CheckAsync(
        string starlinkInterface,
        StarlinkLanAccessSettings settings,
        CancellationToken cancellationToken);

    Task<StarlinkLanAccessCommandResult> RemoveAsync(
        StarlinkLanAccessSettings settings,
        CancellationToken cancellationToken);
}

public sealed class StarlinkLanAccessCommandRunner : IStarlinkLanAccessCommandRunner
{
    public Task<StarlinkLanAccessCommandResult> ApplyAsync(
        string starlinkInterface,
        StarlinkLanAccessSettings settings,
        CancellationToken cancellationToken) =>
        RunAsync("apply", starlinkInterface, settings, cancellationToken);

    public Task<StarlinkLanAccessCommandResult> CheckAsync(
        string starlinkInterface,
        StarlinkLanAccessSettings settings,
        CancellationToken cancellationToken) =>
        RunAsync("check", starlinkInterface, settings, cancellationToken);

    public Task<StarlinkLanAccessCommandResult> RemoveAsync(
        StarlinkLanAccessSettings settings,
        CancellationToken cancellationToken) =>
        RunAsync("remove", null, settings, cancellationToken);

    private static async Task<StarlinkLanAccessCommandResult> RunAsync(
        string action,
        string? starlinkInterface,
        StarlinkLanAccessSettings settings,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return new StarlinkLanAccessCommandResult(false, "Starlink LAN access is only supported on Linux.");
        }

        if (!File.Exists(settings.ApplyHelperPath))
        {
            return new StarlinkLanAccessCommandResult(false, $"Helper is not installed: {settings.ApplyHelperPath}");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.CommandTimeoutSeconds, 3, 60)));

        var startInfo = new ProcessStartInfo
        {
            FileName = File.Exists("/usr/bin/sudo") ? "/usr/bin/sudo" : settings.ApplyHelperPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (startInfo.FileName.EndsWith("sudo", StringComparison.Ordinal))
        {
            startInfo.ArgumentList.Add("-n");
            startInfo.ArgumentList.Add(settings.ApplyHelperPath);
        }

        startInfo.ArgumentList.Add(action);
        if (!string.IsNullOrWhiteSpace(starlinkInterface))
        {
            startInfo.ArgumentList.Add(starlinkInterface);
        }

        startInfo.ArgumentList.Add(settings.LanInterface);
        startInfo.ArgumentList.Add(settings.LanSubnet);
        startInfo.ArgumentList.Add(settings.Destination);

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new StarlinkLanAccessCommandResult(false, "Could not start Starlink LAN access helper.");
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            var output = (await outputTask.ConfigureAwait(false)).Trim();
            var error = (await errorTask.ConfigureAwait(false)).Trim();
            var detail = string.IsNullOrWhiteSpace(error) ? output : error;
            return new StarlinkLanAccessCommandResult(
                process.ExitCode == 0,
                string.IsNullOrWhiteSpace(detail) ? $"Helper exited {process.ExitCode}." : detail);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new StarlinkLanAccessCommandResult(false, "Starlink LAN access helper timed out.");
        }
        catch (Exception ex)
        {
            return new StarlinkLanAccessCommandResult(false, ex.Message);
        }
    }
}

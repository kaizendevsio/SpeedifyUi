using System.Diagnostics;
using System.Text.Json;
using XNetwork.Models;

namespace XNetwork.Services;

public class XBondStatusService(ILogger<XBondStatusService> logger, XBondSettings settings)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public XBondSettings Settings => settings;

    public static XBondStatus ParseStatusJson(string json)
    {
        var status = JsonSerializer.Deserialize<XBondStatus>(json, JsonOptions)
            ?? throw new JsonException("XBond status JSON was empty");
        status.UpdatedAtUtc = DateTime.UtcNow;
        return status;
    }

    public async Task<XBondStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (!settings.Enabled)
        {
            return DisabledStatus("XBond prototype is disabled. Enable it after xbond-client is installed on this host.");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.StatusTimeoutSeconds, 1, 30)));

        try
        {
            var output = await RunStatusCommandAsync(timeoutCts.Token).ConfigureAwait(false);
            return ParseStatusJson(output);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return ErrorStatus($"XBond status timed out after {settings.StatusTimeoutSeconds} seconds");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read XBond status");
            return ErrorStatus(ex.Message);
        }
    }

    private async Task<string> RunStatusCommandAsync(CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = settings.ClientBinaryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("status");
        startInfo.ArgumentList.Add("--config");
        startInfo.ArgumentList.Add(settings.ClientConfigPath);
        startInfo.ArgumentList.Add("--json");

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start xbond-client");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr)
                ? $"xbond-client exited with code {process.ExitCode}"
                : stderr.Trim());
        }

        return stdout;
    }

    private static XBondStatus DisabledStatus(string message)
    {
        return new XBondStatus
        {
            Enabled = false,
            Running = false,
            Mode = "anchor-fec",
            Message = message,
            UpdatedAtUtc = DateTime.UtcNow
        };
    }

    private static XBondStatus ErrorStatus(string error)
    {
        return new XBondStatus
        {
            Enabled = true,
            Running = false,
            Mode = "anchor-fec",
            Message = "XBond status is unavailable",
            Error = error,
            UpdatedAtUtc = DateTime.UtcNow
        };
    }
}

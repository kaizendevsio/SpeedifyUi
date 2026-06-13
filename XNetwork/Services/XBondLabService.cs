using System.Diagnostics;
using System.Text.Json;
using XNetwork.Models;

namespace XNetwork.Services;

public class XBondLabService(
    ILogger<XBondLabService> logger,
    XBondSettings settings,
    SpeedifyService speedifyService)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly SemaphoreSlim _testLock = new(1, 1);

    public XBondSettings Settings => settings;

    public async Task<XBondPublicTestResult> RunPublicHeartbeatTestAsync(CancellationToken cancellationToken = default)
    {
        if (!await _testLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return ErrorResult("Another XBond public test is already running.");
        }

        var result = new XBondPublicTestResult
        {
            StartedAtUtc = DateTime.UtcNow,
            Server = settings.PublicTestServerAddress,
            BypassRule = $"{settings.PublicTestBypassPort}/{settings.PublicTestBypassProtocol}"
        };

        var shouldRestoreBypassEnabled = false;
        try
        {
            var before = await speedifyService.GetStreamingBypassSettingsAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Unable to read Speedify streaming bypass settings.");

            result.BypassWasAlreadyPresent = HasBypassPort(before, settings.PublicTestBypassPort, settings.PublicTestBypassProtocol);

            if (!before.Enabled)
            {
                if (!await speedifyService.SetStreamingBypassEnabledAsync(true, cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidOperationException("Unable to enable Speedify streaming bypass for the test.");
                }

                result.BypassEnabledChanged = true;
                shouldRestoreBypassEnabled = true;
            }

            if (!result.BypassWasAlreadyPresent)
            {
                if (!await speedifyService.SetStreamingBypassPortsAsync(
                        "add",
                        [new PortRule { Port = settings.PublicTestBypassPort, Protocol = settings.PublicTestBypassProtocol }],
                        cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidOperationException($"Unable to add temporary bypass rule {result.BypassRule}.");
                }

                result.BypassAdded = true;
            }

            var key = ReadPsk();
            var ping = await RunPingAsync(key, cancellationToken).ConfigureAwait(false);
            CopyPingResult(ping, result);
            result.Message = result.Succeeded
                ? "Public heartbeat completed successfully."
                : "Public heartbeat completed with packet loss.";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "XBond public heartbeat test failed");
            result.Error = ex.Message;
            result.Message = "Public heartbeat test failed.";
        }
        finally
        {
            if (result.BypassAdded)
            {
                result.BypassRemoved = await speedifyService.SetStreamingBypassPortsAsync(
                    "rem",
                    [new PortRule { Port = settings.PublicTestBypassPort, Protocol = settings.PublicTestBypassProtocol }],
                    CancellationToken.None).ConfigureAwait(false);
            }

            if (shouldRestoreBypassEnabled)
            {
                result.BypassEnabledRestored = await speedifyService.SetStreamingBypassEnabledAsync(false, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            result.CompletedAtUtc = DateTime.UtcNow;
            _testLock.Release();
        }

        return result;
    }

    public static XBondPublicTestResult ParsePingJson(string json)
    {
        return JsonSerializer.Deserialize<XBondPublicTestResult>(json, JsonOptions)
            ?? throw new JsonException("XBond ping JSON was empty.");
    }

    public static bool HasBypassPort(StreamingBypassSettings bypassSettings, int port, string protocol)
    {
        return bypassSettings.Ports.Any(rule =>
            rule.Port == port &&
            (!rule.PortRangeEnd.HasValue || rule.PortRangeEnd.Value is 0 || rule.PortRangeEnd.Value == port) &&
            string.Equals(rule.Protocol, protocol, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<XBondPublicTestResult> RunPingAsync(string key, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.PublicTestCommandTimeoutSeconds, 5, 120)));

        var startInfo = new ProcessStartInfo
        {
            FileName = settings.ClientBinaryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.Environment[settings.PublicTestKeyEnvironmentVariable] = key;
        startInfo.ArgumentList.Add("ping");
        startInfo.ArgumentList.Add("--server");
        startInfo.ArgumentList.Add(settings.PublicTestServerAddress);
        startInfo.ArgumentList.Add("--path-id");
        startInfo.ArgumentList.Add(settings.PublicTestPathId.ToString());
        startInfo.ArgumentList.Add("--count");
        startInfo.ArgumentList.Add(settings.PublicTestCount.ToString());
        startInfo.ArgumentList.Add("--interval-ms");
        startInfo.ArgumentList.Add(settings.PublicTestIntervalMs.ToString());
        startInfo.ArgumentList.Add("--timeout-ms");
        startInfo.ArgumentList.Add(settings.PublicTestPacketTimeoutMs.ToString());
        startInfo.ArgumentList.Add("--key-env");
        startInfo.ArgumentList.Add(settings.PublicTestKeyEnvironmentVariable);
        startInfo.ArgumentList.Add("--json");

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start xbond-client.");
        }

        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr)
                    ? $"xbond-client exited with code {process.ExitCode}"
                    : stderr.Trim());
            }

            return ParsePingJson(stdout);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException($"xbond-client ping timed out after {settings.PublicTestCommandTimeoutSeconds} seconds.");
        }
        catch
        {
            TryKill(process);
            throw;
        }
    }

    private string ReadPsk()
    {
        var envValue = Environment.GetEnvironmentVariable(settings.PublicTestKeyEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(envValue))
        {
            return envValue.Trim();
        }

        if (!string.IsNullOrWhiteSpace(settings.PublicTestKeyFilePath) && File.Exists(settings.PublicTestKeyFilePath))
        {
            var fileValue = File.ReadAllText(settings.PublicTestKeyFilePath).Trim();
            if (!string.IsNullOrWhiteSpace(fileValue))
            {
                return fileValue;
            }
        }

        throw new InvalidOperationException("XBond public test key is not configured.");
    }

    private static XBondPublicTestResult ErrorResult(string message)
    {
        return new XBondPublicTestResult
        {
            Error = message,
            Message = message,
            CompletedAtUtc = DateTime.UtcNow
        };
    }

    private static void CopyPingResult(XBondPublicTestResult source, XBondPublicTestResult target)
    {
        target.Server = source.Server;
        target.Bind = source.Bind;
        target.PathId = source.PathId;
        target.SessionId = source.SessionId;
        target.Sent = source.Sent;
        target.Received = source.Received;
        target.Lost = source.Lost;
        target.LossRate = source.LossRate;
        target.MinRttMs = source.MinRttMs;
        target.AvgRttMs = source.AvgRttMs;
        target.MaxRttMs = source.MaxRttMs;
        target.Replies = source.Replies;
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
            // Best effort cleanup for a failed lab command.
        }
    }
}

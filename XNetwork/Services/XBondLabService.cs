using System.Diagnostics;
using System.Text.Json;
using XNetwork.Models;

namespace XNetwork.Services;

public class XBondLabService(
    ILogger<XBondLabService> logger,
    XBondSettings settings,
    XBondStatsService xbondStatsService)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private const string ServiceEnvironmentFilePath = "/etc/xbond/client.env";
    private readonly SemaphoreSlim _testLock = new(1, 1);

    public XBondSettings Settings => settings;

    public async Task<XBondPublicTestResult> RunPublicHeartbeatTestAsync(CancellationToken cancellationToken = default)
    {
        if (!await _testLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return ErrorResult("Another XBond diagnostic is already running.");
        }

        var result = new XBondPublicTestResult
        {
            StartedAtUtc = DateTime.UtcNow,
            Server = settings.PublicTestServerAddress
        };

        try
        {
            var key = ReadPsk();
            var ping = await RunPingAsync(key, cancellationToken).ConfigureAwait(false);
            CopyPingResult(ping, result);
            result.Message = result.Succeeded
                ? "XBond heartbeat completed successfully."
                : "XBond heartbeat completed with packet loss.";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "XBond heartbeat diagnostic failed");
            result.Error = ex.Message;
            result.Message = "XBond heartbeat diagnostic failed.";
        }
        finally
        {
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

    public static XBondProbeResult ParseMultiPingJson(string json)
    {
        return JsonSerializer.Deserialize<XBondProbeResult>(json, JsonOptions)
            ?? throw new JsonException("XBond multi-ping JSON was empty.");
    }

    public async Task<XBondProbeResult> RunPublicMultiPathTestAsync(CancellationToken cancellationToken = default)
    {
        if (!await _testLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return ErrorProbeResult("Another XBond diagnostic is already running.");
        }

        var result = new XBondProbeResult
        {
            StartedAtUtc = DateTime.UtcNow,
            Server = settings.PublicTestServerAddress
        };

        try
        {
            var key = ReadPsk();
            var probe = await RunMultiPingAsync(key, cancellationToken).ConfigureAwait(false);
            CopyProbeResult(probe, result);
            result.Message = result.FullyVerified
                ? "Multi-path diagnostic completed on verified physical routes."
                : result.Succeeded
                    ? "Multi-path diagnostic completed with route-verification warnings."
                    : result.HasAnyPathResponse
                        ? "Multi-path diagnostic completed with path loss or degradation."
                        : "Multi-path diagnostic completed with packet loss.";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "XBond multi-path diagnostic failed");
            result.Error = ex.Message;
            result.Message = "Multi-path diagnostic failed.";
        }
        finally
        {
            result.CompletedAtUtc = DateTime.UtcNow;
            _testLock.Release();
        }

        return result;
    }

    private async Task<XBondPublicTestResult> RunPingAsync(string key, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.PublicTestCommandTimeoutSeconds, 5, 120)));
        var diagnosticPath = await SelectHeartbeatPathAsync(timeoutCts.Token).ConfigureAwait(false);
        var pathId = diagnosticPath?.PathId > 0 ? diagnosticPath.PathId : settings.PublicTestPathId;

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
        if (!string.IsNullOrWhiteSpace(diagnosticPath?.InterfaceName))
        {
            startInfo.ArgumentList.Add("--interface");
            startInfo.ArgumentList.Add(diagnosticPath.InterfaceName);
        }

        startInfo.ArgumentList.Add("--path-id");
        startInfo.ArgumentList.Add(pathId.ToString());
        startInfo.ArgumentList.Add("--count");
        startInfo.ArgumentList.Add(settings.PublicTestCount.ToString());
        startInfo.ArgumentList.Add("--interval-ms");
        startInfo.ArgumentList.Add(settings.PublicTestIntervalMs.ToString());
        startInfo.ArgumentList.Add("--timeout-ms");
        startInfo.ArgumentList.Add(settings.PublicTestPacketTimeoutMs.ToString());
        startInfo.ArgumentList.Add("--key-env");
        startInfo.ArgumentList.Add(settings.PublicTestKeyEnvironmentVariable);
        startInfo.ArgumentList.Add("--json");

        return await RunJsonCommandAsync(startInfo, ParsePingJson, timeoutCts.Token).ConfigureAwait(false);
    }

    private async Task<XBondProbeResult> RunMultiPingAsync(string key, CancellationToken cancellationToken)
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
        startInfo.ArgumentList.Add("multi-ping");
        startInfo.ArgumentList.Add("--config");
        startInfo.ArgumentList.Add(settings.ClientConfigPath);
        startInfo.ArgumentList.Add("--server");
        startInfo.ArgumentList.Add(settings.PublicTestServerAddress);

        var pathIds = await SelectMultiPathIdsAsync(timeoutCts.Token).ConfigureAwait(false);
        foreach (var pathId in pathIds)
        {
            startInfo.ArgumentList.Add("--path-id");
            startInfo.ArgumentList.Add(pathId.ToString());
        }

        foreach (var bind in settings.PublicTestBinds.Where(bind => !string.IsNullOrWhiteSpace(bind)))
        {
            startInfo.ArgumentList.Add("--bind");
            startInfo.ArgumentList.Add(bind.Trim());
        }

        startInfo.ArgumentList.Add("--count");
        startInfo.ArgumentList.Add(settings.PublicTestCount.ToString());
        startInfo.ArgumentList.Add("--interval-ms");
        startInfo.ArgumentList.Add(settings.PublicTestIntervalMs.ToString());
        startInfo.ArgumentList.Add("--timeout-ms");
        startInfo.ArgumentList.Add(settings.PublicTestPacketTimeoutMs.ToString());
        startInfo.ArgumentList.Add("--key-env");
        startInfo.ArgumentList.Add(settings.PublicTestKeyEnvironmentVariable);
        startInfo.ArgumentList.Add("--json");

        return await RunJsonCommandAsync(startInfo, ParseMultiPingJson, timeoutCts.Token).ConfigureAwait(false);
    }

    private static async Task<T> RunJsonCommandAsync<T>(
        ProcessStartInfo startInfo,
        Func<string, T> parser,
        CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start {startInfo.FileName}.");
        }

        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr)
                    ? $"{startInfo.FileName} exited with code {process.ExitCode}"
                    : stderr.Trim());
            }

            return parser(stdout);
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

        foreach (var path in PskCandidatePaths())
        {
            var fileValue = ReadPskFromPath(path);
            if (!string.IsNullOrWhiteSpace(fileValue))
            {
                return fileValue;
            }
        }

        throw new InvalidOperationException("XBond diagnostic key is not configured.");
    }

    private async Task<XBondPathStatsSnapshot?> SelectHeartbeatPathAsync(CancellationToken cancellationToken)
    {
        var snapshot = await xbondStatsService.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return snapshot.ActivePaths
            .Where(path => path.PathId > 0 && path.InterfaceUp)
            .OrderBy(path => path.IsAnchor ? 0 : 1)
            .ThenBy(path => path.RttMs ?? double.MaxValue)
            .ThenBy(path => path.PathId)
            .FirstOrDefault();
    }

    private async Task<IReadOnlyList<int>> SelectMultiPathIdsAsync(CancellationToken cancellationToken)
    {
        var configuredPathIds = settings.PublicTestPathIds
            .Where(pathId => pathId > 0)
            .Distinct()
            .ToArray();
        if (configuredPathIds.Length > 0)
        {
            return configuredPathIds;
        }

        var snapshot = await xbondStatsService.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return snapshot.ActivePaths
            .Where(path => path.PathId > 0 && path.InterfaceUp)
            .OrderBy(path => path.IsAnchor ? 0 : 1)
            .ThenBy(path => path.RttMs ?? double.MaxValue)
            .ThenBy(path => path.PathId)
            .Select(path => path.PathId)
            .Distinct()
            .ToArray();
    }

    private IEnumerable<string> PskCandidatePaths()
    {
        yield return ServiceEnvironmentFilePath;

        if (!string.IsNullOrWhiteSpace(settings.PublicTestKeyFilePath) &&
            !string.Equals(settings.PublicTestKeyFilePath, ServiceEnvironmentFilePath, StringComparison.Ordinal))
        {
            yield return settings.PublicTestKeyFilePath;
        }
    }

    private string? ReadPskFromPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            if (File.Exists(path))
            {
                return ExtractPsk(File.ReadAllText(path));
            }
        }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsLinux())
        {
            return ReadPskWithSudo(path);
        }
        catch (IOException) when (OperatingSystem.IsLinux())
        {
            return ReadPskWithSudo(path);
        }

        return null;
    }

    private string? ReadPskWithSudo(string path)
    {
        var sudoPath = string.IsNullOrWhiteSpace(settings.SudoPath) ? "sudo" : settings.SudoPath;
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = sudoPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("-n");
        process.StartInfo.ArgumentList.Add("/bin/cat");
        process.StartInfo.ArgumentList.Add(path);

        try
        {
            if (!process.Start())
            {
                return null;
            }

            if (!process.WaitForExit(5_000))
            {
                TryKill(process);
                return null;
            }

            if (process.ExitCode != 0)
            {
                logger.LogDebug("Unable to read XBond diagnostic key from {Path} with sudo: {Error}", path, process.StandardError.ReadToEnd().Trim());
                return null;
            }

            return ExtractPsk(process.StandardOutput.ReadToEnd());
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Unable to read XBond diagnostic key from {Path} with sudo", path);
            return null;
        }
    }

    public static string? ExtractPsk(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith("XBOND_PSK=", StringComparison.Ordinal))
            {
                continue;
            }

            var value = line["XBOND_PSK=".Length..].Trim().Trim('"');
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        var trimmed = content.Trim();
        return trimmed.Contains('=', StringComparison.Ordinal) ? null : trimmed;
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

    private static XBondProbeResult ErrorProbeResult(string message)
    {
        return new XBondProbeResult
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

    private static void CopyProbeResult(XBondProbeResult source, XBondProbeResult target)
    {
        target.Mode = source.Mode;
        target.AnchorPathId = source.AnchorPathId;
        target.DuplicatePathIds = source.DuplicatePathIds;
        target.StartedAtMicros = source.StartedAtMicros;
        target.CompletedAtMicros = source.CompletedAtMicros;
        target.Paths = source.Paths;
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
            // Best effort cleanup for a failed diagnostic command.
        }
    }
}

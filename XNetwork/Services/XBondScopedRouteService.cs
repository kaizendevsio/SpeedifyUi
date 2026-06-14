using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using XNetwork.Models;

namespace XNetwork.Services;

public partial class XBondScopedRouteService(
    ILogger<XBondScopedRouteService> logger,
    XBondSettings settings)
{
    private readonly SemaphoreSlim _routeLock = new(1, 1);

    public XBondSettings Settings => settings;

    public async Task<XBondScopedRouteStatus> GetStatusAsync(string? target = null, CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeTarget(target ?? settings.ScopedRouteDefaultTarget, out var normalizedTarget, out var error))
        {
            return ErrorStatus(target ?? "", error);
        }

        var status = BaseStatus(normalizedTarget);

        if (!OperatingSystem.IsLinux())
        {
            status.Error = "Scoped XBond routes are only available on Linux.";
            status.Message = status.Error;
            return status;
        }

        var result = await RunCommandAsync(
            settings.RouteCommandPath,
            ["route", "get", normalizedTarget],
            useSudo: false,
            timeoutSeconds: settings.ScopedRouteCommandTimeoutSeconds,
            cancellationToken).ConfigureAwait(false);

        status.RouteOutput = result.Output;
        status.RouteUsesXBond = result.ExitCode == 0 &&
                                result.Output.Contains($"dev {settings.TunnelDevice}", StringComparison.Ordinal);
        status.Message = status.RouteUsesXBond
            ? $"Scoped route for {status.TargetCidr} uses {settings.TunnelDevice}."
            : $"Scoped route for {status.TargetCidr} is not active on {settings.TunnelDevice}.";

        if (result.ExitCode != 0)
        {
            status.Error = string.IsNullOrWhiteSpace(result.Output)
                ? $"ip route get exited with code {result.ExitCode}."
                : result.Output;
        }

        return status;
    }

    public async Task<XBondScopedRouteStatus> ApplyScopedRouteAsync(string target, CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeTarget(target, out var normalizedTarget, out var error))
        {
            return ErrorStatus(target, error);
        }

        var status = BaseStatus(normalizedTarget);

        if (!OperatingSystem.IsLinux())
        {
            status.Error = "Scoped XBond routes are only available on Linux.";
            status.Message = status.Error;
            return status;
        }

        if (!await _routeLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            status.Error = "Another scoped XBond route operation is already running.";
            status.Message = status.Error;
            return status;
        }

        try
        {
            var result = await ReplaceRouteAsync(normalizedTarget, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                status.Error = string.IsNullOrWhiteSpace(result.Output)
                    ? $"ip route replace exited with code {result.ExitCode}."
                    : result.Output;
                status.Message = "Unable to apply scoped XBond route.";
                return status;
            }

            return await GetStatusAsync(normalizedTarget, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to apply XBond scoped route to {Target}", normalizedTarget);
            status.Error = ex.Message;
            status.Message = "Unable to apply scoped XBond route.";
            return status;
        }
        finally
        {
            _routeLock.Release();
        }
    }

    public async Task<XBondScopedRouteStatus> ClearScopedRouteAsync(string target, CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeTarget(target, out var normalizedTarget, out var error))
        {
            return ErrorStatus(target, error);
        }

        var status = BaseStatus(normalizedTarget);

        if (!OperatingSystem.IsLinux())
        {
            status.Error = "Scoped XBond routes are only available on Linux.";
            status.Message = status.Error;
            return status;
        }

        if (!await _routeLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            status.Error = "Another scoped XBond route operation is already running.";
            status.Message = status.Error;
            return status;
        }

        try
        {
            await DeleteRouteAsync(normalizedTarget, cancellationToken).ConfigureAwait(false);
            return await GetStatusAsync(normalizedTarget, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to clear XBond scoped route to {Target}", normalizedTarget);
            status.Error = ex.Message;
            status.Message = "Unable to clear scoped XBond route.";
            return status;
        }
        finally
        {
            _routeLock.Release();
        }
    }

    public async Task<XBondScopedRouteTestResult> RunScopedRouteTestAsync(string target, CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeTarget(target, out var normalizedTarget, out var error))
        {
            return new XBondScopedRouteTestResult
            {
                Target = target,
                TunnelDevice = settings.TunnelDevice,
                SourceAddress = settings.TunnelSource,
                Error = error,
                Message = error,
                UpdatedAtUtc = DateTime.UtcNow
            };
        }

        var result = new XBondScopedRouteTestResult
        {
            Target = normalizedTarget,
            TunnelDevice = settings.TunnelDevice,
            SourceAddress = settings.TunnelSource,
            UpdatedAtUtc = DateTime.UtcNow
        };

        if (!OperatingSystem.IsLinux())
        {
            result.Error = "Scoped XBond route tests are only available on Linux.";
            result.Message = result.Error;
            return result;
        }

        if (!await _routeLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            result.Error = "Another scoped XBond route operation is already running.";
            result.Message = result.Error;
            return result;
        }

        try
        {
            var routeResult = await ReplaceRouteAsync(normalizedTarget, cancellationToken).ConfigureAwait(false);
            if (routeResult.ExitCode != 0)
            {
                result.Error = string.IsNullOrWhiteSpace(routeResult.Output)
                    ? $"ip route replace exited with code {routeResult.ExitCode}."
                    : routeResult.Output;
                result.Message = "Unable to apply scoped XBond route for the test.";
                return result;
            }

            var ping = await RunCommandAsync(
                settings.PingCommandPath,
                [
                    "-I",
                    settings.TunnelDevice,
                    "-c",
                    settings.ScopedRouteTestCount.ToString(),
                    "-W",
                    settings.ScopedRoutePacketTimeoutSeconds.ToString(),
                    normalizedTarget
                ],
                useSudo: false,
                timeoutSeconds: settings.ScopedRouteCommandTimeoutSeconds,
                cancellationToken).ConfigureAwait(false);

            ApplyPingOutput(result, ping.Output);
            result.RouteOutput = (await GetStatusAsync(normalizedTarget, cancellationToken).ConfigureAwait(false)).RouteOutput;
            result.RouteUsesXBond = result.RouteOutput.Contains($"dev {settings.TunnelDevice}", StringComparison.Ordinal);
            result.Message = result.Succeeded
                ? "Scoped XBond route test passed and the temporary route was removed."
                : "Scoped XBond route test completed with loss or errors; the temporary route was removed.";

            if (ping.ExitCode != 0 && result.Sent == 0)
            {
                result.Error = string.IsNullOrWhiteSpace(ping.Output)
                    ? $"ping exited with code {ping.ExitCode}."
                    : ping.Output;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to run XBond scoped route test to {Target}", normalizedTarget);
            result.Error = ex.Message;
            result.Message = "Scoped XBond route test failed; cleanup was attempted.";
        }
        finally
        {
            try
            {
                var deleteResult = await DeleteRouteAsync(normalizedTarget, CancellationToken.None).ConfigureAwait(false);
                result.RouteRemoved = deleteResult.ExitCode == 0 ||
                                      deleteResult.Output.Contains("No such process", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to remove temporary XBond scoped route to {Target}", normalizedTarget);
                result.RouteRemoved = false;
            }

            _routeLock.Release();
        }

        return result;
    }

    public static string NormalizeTarget(string target)
    {
        var trimmed = target.Trim();
        if (trimmed.Contains('/'))
        {
            throw new ArgumentException("Only a single IPv4 address is allowed for scoped XBond routes.");
        }

        if (!IPAddress.TryParse(trimmed, out var address) ||
            address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
            Equals(address, IPAddress.Any) ||
            Equals(address, IPAddress.Broadcast))
        {
            throw new ArgumentException("Enter a valid unicast IPv4 address.");
        }

        return address.ToString();
    }

    private static bool TryNormalizeTarget(string target, out string normalizedTarget, out string error)
    {
        try
        {
            normalizedTarget = NormalizeTarget(target);
            error = "";
            return true;
        }
        catch (ArgumentException ex)
        {
            normalizedTarget = target.Trim();
            error = ex.Message;
            return false;
        }
    }

    public static void ApplyPingOutput(XBondScopedRouteTestResult result, string output)
    {
        var packetMatch = PacketSummaryRegex().Match(output);
        if (packetMatch.Success)
        {
            result.Sent = int.Parse(packetMatch.Groups["sent"].Value);
            result.Received = int.Parse(packetMatch.Groups["received"].Value);
            result.Lost = Math.Max(0, result.Sent - result.Received);
            result.LossRate = result.Sent == 0 ? 1 : result.Lost / (double)result.Sent;
        }

        var rttMatch = RttSummaryRegex().Match(output);
        if (rttMatch.Success)
        {
            result.MinRttMs = double.Parse(rttMatch.Groups["min"].Value, CultureInfo.InvariantCulture);
            result.AvgRttMs = double.Parse(rttMatch.Groups["avg"].Value, CultureInfo.InvariantCulture);
            result.MaxRttMs = double.Parse(rttMatch.Groups["max"].Value, CultureInfo.InvariantCulture);
        }
    }

    private async Task<CommandResult> ReplaceRouteAsync(string target, CancellationToken cancellationToken)
    {
        return await RunCommandAsync(
            settings.RouteCommandPath,
            ["route", "replace", $"{target}/32", "dev", settings.TunnelDevice, "src", settings.TunnelSource],
            useSudo: true,
            timeoutSeconds: settings.ScopedRouteCommandTimeoutSeconds,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<CommandResult> DeleteRouteAsync(string target, CancellationToken cancellationToken)
    {
        return await RunCommandAsync(
            settings.RouteCommandPath,
            ["route", "del", $"{target}/32", "dev", settings.TunnelDevice],
            useSudo: true,
            timeoutSeconds: settings.ScopedRouteCommandTimeoutSeconds,
            cancellationToken).ConfigureAwait(false);
    }

    private XBondScopedRouteStatus BaseStatus(string target)
    {
        return new XBondScopedRouteStatus
        {
            Target = target,
            TunnelDevice = settings.TunnelDevice,
            SourceAddress = settings.TunnelSource,
            UpdatedAtUtc = DateTime.UtcNow
        };
    }

    private XBondScopedRouteStatus ErrorStatus(string target, string error)
    {
        return new XBondScopedRouteStatus
        {
            Target = target,
            TunnelDevice = settings.TunnelDevice,
            SourceAddress = settings.TunnelSource,
            Error = error,
            Message = error,
            UpdatedAtUtc = DateTime.UtcNow
        };
    }

    private async Task<CommandResult> RunCommandAsync(
        string command,
        IReadOnlyList<string> arguments,
        bool useSudo,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 3, 120)));

        var startInfo = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (useSudo)
        {
            startInfo.FileName = settings.SudoPath;
            startInfo.ArgumentList.Add("-n");
            startInfo.ArgumentList.Add(command);
        }
        else
        {
            startInfo.FileName = command;
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start {command}.");
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

            return new CommandResult(process.ExitCode, output);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException($"{command} timed out after {timeoutSeconds} seconds.");
        }
        catch
        {
            TryKill(process);
            throw;
        }
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
            // Best effort cleanup for a failed route command.
        }
    }

    [GeneratedRegex(@"(?<sent>\d+)\s+packets transmitted,\s+(?<received>\d+)\s+(?:packets\s+)?received", RegexOptions.IgnoreCase)]
    private static partial Regex PacketSummaryRegex();

    [GeneratedRegex(@"(?:rtt|round-trip).*?=\s*(?<min>[\d.]+)/(?<avg>[\d.]+)/(?<max>[\d.]+)/", RegexOptions.IgnoreCase)]
    private static partial Regex RttSummaryRegex();

    private sealed record CommandResult(int ExitCode, string Output);
}

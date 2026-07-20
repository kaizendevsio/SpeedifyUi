using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using XNetwork.Models;

namespace XNetwork.Services;

public sealed partial class XBondPhysicalPathProbeService(
    XBondSettings xbondSettings,
    ILogger<XBondPhysicalPathProbeService> logger)
{
    public async Task<IReadOnlyList<XBondPhysicalPathProbeResult>> ProbeAsync(
        IEnumerable<string> interfaceNames,
        string target,
        XBondClientWatchdogSettings settings,
        CancellationToken cancellationToken = default)
    {
        var interfaces = interfaceNames
            .Where(IsSafePhysicalInterface)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return await Task.WhenAll(interfaces.Select(interfaceName =>
            ProbeOneAsync(interfaceName, target, settings, cancellationToken))).ConfigureAwait(false);
    }

    private async Task<XBondPhysicalPathProbeResult> ProbeOneAsync(
        string interfaceName,
        string target,
        XBondClientWatchdogSettings settings,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return new XBondPhysicalPathProbeResult
            {
                InterfaceName = interfaceName,
                Target = target,
                Error = "Interface-bound probes are supported only on Linux."
            };
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = string.IsNullOrWhiteSpace(xbondSettings.PingCommandPath) ? "ping" : xbondSettings.PingCommandPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-I");
        startInfo.ArgumentList.Add(interfaceName);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(settings.ProbeCount.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-W");
        startInfo.ArgumentList.Add(settings.ProbeTimeoutSeconds.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(target);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(
            Math.Clamp(settings.ProbeCount * (settings.ProbeTimeoutSeconds + 1) + 3, 6, 30)));
        using var process = new Process { StartInfo = startInfo };

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Could not start the interface-bound ping probe.");
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            var output = await stdoutTask.ConfigureAwait(false);
            var error = await stderrTask.ConfigureAwait(false);
            var parsed = ParsePingOutput(interfaceName, target, output, process.ExitCode);
            parsed.Error = parsed.Responded
                ? null
                : FirstNonEmpty(error, output, $"ping exited with code {process.ExitCode}");
            parsed.Healthy = parsed.Responded &&
                             parsed.LossPercent <= settings.PhysicalMaxLossPercent &&
                             parsed.AverageRttMs.HasValue &&
                             parsed.AverageRttMs.Value <= settings.PhysicalMaxRttMs;
            return parsed;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return new XBondPhysicalPathProbeResult
            {
                InterfaceName = interfaceName,
                Target = target,
                Error = "Interface-bound ping timed out."
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TryKill(process);
            logger.LogDebug(ex, "Physical path probe failed on {Interface}", interfaceName);
            return new XBondPhysicalPathProbeResult
            {
                InterfaceName = interfaceName,
                Target = target,
                Error = ex.Message
            };
        }
    }

    public static XBondPhysicalPathProbeResult ParsePingOutput(
        string interfaceName,
        string target,
        string output,
        int exitCode)
    {
        var lossMatch = PacketLossRegex().Match(output);
        var rttMatch = RttRegex().Match(output);
        var loss = lossMatch.Success && double.TryParse(
            lossMatch.Groups[1].Value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var parsedLoss)
            ? parsedLoss
            : 100;
        double? averageRtt = null;
        if (rttMatch.Success && double.TryParse(
                rttMatch.Groups[1].Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsedRtt))
        {
            averageRtt = parsedRtt;
        }

        return new XBondPhysicalPathProbeResult
        {
            InterfaceName = interfaceName,
            Target = target,
            Responded = exitCode == 0 && averageRtt.HasValue && loss < 100,
            AverageRttMs = averageRtt,
            LossPercent = loss
        };
    }

    public static bool IsSafePhysicalInterface(string? interfaceName)
    {
        if (string.IsNullOrWhiteSpace(interfaceName) || !InterfaceNameRegex().IsMatch(interfaceName))
        {
            return false;
        }

        return !new[] { "lo", "xbond", "tailscale", "tun", "tap", "wg", "docker", "br-", "veth" }
            .Any(prefix => interfaceName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static string? FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

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
            // Best effort cleanup.
        }
    }

    [GeneratedRegex(@"([0-9]+(?:\.[0-9]+)?)%\s+packet loss", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PacketLossRegex();

    [GeneratedRegex(@"(?:rtt|round-trip) min/avg/max/(?:mdev|stddev)\s*=\s*[^/]+/([0-9]+(?:\.[0-9]+)?)/", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RttRegex();

    [GeneratedRegex(@"^[a-zA-Z0-9_.:-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex InterfaceNameRegex();
}

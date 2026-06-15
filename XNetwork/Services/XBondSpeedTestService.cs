using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using XNetwork.Models;

namespace XNetwork.Services;

public sealed class XBondSpeedTestService(
    ILogger<XBondSpeedTestService> logger,
    XBondSettings settings,
    XBondStatusService statusService)
{
    private static readonly JsonSerializerOptions ArtifactJsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim _testLock = new(1, 1);

    public async Task<XBondSpeedTestResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var result = new XBondSpeedTestResult
        {
            StartedAtUtc = DateTime.UtcNow
        };

        if (!OperatingSystem.IsLinux())
        {
            result.Error = "XBond speed tests are only available on Linux.";
            result.Message = result.Error;
            result.CompletedAtUtc = DateTime.UtcNow;
            return result;
        }

        if (!await _testLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            result.Error = "Another XBond speed test is already running.";
            result.Message = result.Error;
            result.CompletedAtUtc = DateTime.UtcNow;
            return result;
        }

        try
        {
            result.XBondServerHost = settings.ServerSpeedTestHost;
            result.XBondServerPort = settings.ServerSpeedTestPort;

            var route = await RunCommandAsync(
                settings.RouteCommandPath,
                ["route", "get", "1.1.1.1"],
                timeoutSeconds: Math.Min(settings.ScopedRouteCommandTimeoutSeconds, 15),
                cancellationToken).ConfigureAwait(false);

            result.RouteOutput = route.Output;
            result.RouteUsesXBond = route.ExitCode == 0 &&
                                    route.Output.Contains($"dev {settings.TunnelDevice}", StringComparison.Ordinal);

            await RunServerSpeedTestAsync(result, cancellationToken).ConfigureAwait(false);
            await RunPublicSpeedTestAsync(result, cancellationToken).ConfigureAwait(false);

            if (!result.ServerTestSucceeded && !result.PublicTestSucceeded)
            {
                result.Error = "Both XBond server and public speed tests failed.";
                result.Message = "XBond speed tests failed.";
                return result;
            }

            result.Message = result.RouteUsesXBond
                ? result.Succeeded
                    ? "XBond speed tests completed."
                    : "XBond speed tests completed with partial results."
                : $"Speed tests completed, but the default route was not using {settings.TunnelDevice}.";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "XBond speed test failed");
            result.Error = ex.Message;
            result.Message = "XBond speed test failed.";
        }
        finally
        {
            result.CompletedAtUtc = DateTime.UtcNow;
            _testLock.Release();
        }

        return result;
    }

    public async Task<XBondSpeedTestResult> RunBadNetworkSimulationAsync(CancellationToken cancellationToken = default)
    {
        var result = new XBondSpeedTestResult
        {
            StartedAtUtc = DateTime.UtcNow,
            IsSimulation = true
        };

        if (!OperatingSystem.IsLinux())
        {
            result.Error = "XBond network simulations are only available on Linux.";
            result.Message = result.Error;
            result.CompletedAtUtc = DateTime.UtcNow;
            return result;
        }

        if (!await _testLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            result.Error = "Another XBond speed test is already running.";
            result.Message = result.Error;
            result.CompletedAtUtc = DateTime.UtcNow;
            return result;
        }

        string? simulatedInterface = null;
        try
        {
            var status = await statusService.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            var backup = status.Paths
                .Where(path => string.Equals(path.Role, "backup", StringComparison.OrdinalIgnoreCase))
                .Where(path => path.InterfaceUp && !string.IsNullOrWhiteSpace(path.InterfaceName))
                .OrderByDescending(path => path.LossRate)
                .ThenByDescending(path => path.RttMs ?? 0)
                .FirstOrDefault();

            if (backup is null)
            {
                result.Error = "No live backup path is available for degraded-path simulation.";
                result.Message = result.Error;
                return result;
            }

            simulatedInterface = backup.InterfaceName!;
            result.SimulatedInterface = simulatedInterface;
            result.SimulationProfile =
                $"delay {settings.SimulationDelayMs}ms {settings.SimulationJitterMs}ms loss {settings.SimulationLossPercent:0.#}% rate {settings.SimulationRateLimit}";
            result.XBondServerHost = settings.ServerSpeedTestHost;
            result.XBondServerPort = settings.ServerSpeedTestPort;

            var tcApply = await RunCommandAsync(
                settings.SudoPath,
                [
                    "-n",
                    settings.TrafficControlCommandPath,
                    "qdisc",
                    "replace",
                    "dev",
                    simulatedInterface,
                    "root",
                    "netem",
                    "delay",
                    $"{settings.SimulationDelayMs}ms",
                    $"{settings.SimulationJitterMs}ms",
                    "loss",
                    $"{settings.SimulationLossPercent.ToString("0.#", CultureInfo.InvariantCulture)}%",
                    "rate",
                    settings.SimulationRateLimit
                ],
                timeoutSeconds: 15,
                cancellationToken).ConfigureAwait(false);
            if (tcApply.ExitCode != 0)
            {
                result.Error = string.IsNullOrWhiteSpace(tcApply.Output)
                    ? $"Unable to apply netem on {simulatedInterface}."
                    : tcApply.Output;
                result.Message = "XBond degraded-path simulation failed.";
                return result;
            }

            await RunServerSpeedTestAsync(result, cancellationToken).ConfigureAwait(false);
            result.Message = result.ServerTestSucceeded
                ? "Degraded backup simulation completed."
                : "Degraded backup simulation completed with partial results.";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "XBond degraded-path simulation failed");
            result.Error = ex.Message;
            result.Message = "XBond degraded-path simulation failed.";
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(simulatedInterface))
            {
                try
                {
                    await RunCommandAsync(
                        settings.SudoPath,
                        [
                            "-n",
                            settings.TrafficControlCommandPath,
                            "qdisc",
                            "del",
                            "dev",
                            simulatedInterface,
                            "root"
                        ],
                        timeoutSeconds: 15,
                        cancellationToken).ConfigureAwait(false);
                    result.SimulationCleanupSucceeded = true;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to cleanup XBond degraded-path simulation on {Interface}", simulatedInterface);
                    result.SimulationCleanupSucceeded = false;
                    result.Error = string.IsNullOrWhiteSpace(result.Error)
                        ? $"Simulation cleanup failed: {ex.Message}"
                        : $"{result.Error} Cleanup failed: {ex.Message}";
                }
            }

            result.CompletedAtUtc = DateTime.UtcNow;
            _testLock.Release();
        }

        return result;
    }

    public async Task<XBondPerformanceMatrixResult> RunPerformanceMatrixAsync(CancellationToken cancellationToken = default)
    {
        var result = new XBondPerformanceMatrixResult
        {
            StartedAtUtc = DateTime.UtcNow
        };

        if (!OperatingSystem.IsLinux())
        {
            result.Error = "XBond performance matrix is only available on Linux.";
            result.Message = result.Error;
            result.CompletedAtUtc = DateTime.UtcNow;
            return result;
        }

        if (!await _testLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            result.Error = "Another XBond diagnostic is already running.";
            result.Message = result.Error;
            result.CompletedAtUtc = DateTime.UtcNow;
            return result;
        }

        var overrideActive = false;
        try
        {
            result.Before = await CaptureSystemSampleAsync(cancellationToken).ConfigureAwait(false);

            var status = await statusService.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            foreach (var path in status.Paths.Where(path => path.InterfaceUp && !string.IsNullOrWhiteSpace(path.InterfaceName)))
            {
                result.Runs.Add(await RunNativeAdapterMatrixRunAsync(path, cancellationToken).ConfigureAwait(false));
            }

            foreach (var (mode, name) in new[]
                     {
                         ("anchor-only", "XBond anchor only"),
                         ("anchor-duplicate-1", "XBond anchor + 1 duplicate"),
                         ("anchor-fec", "XBond anchor + FEC")
                     })
            {
                await SetDiagnosticOverrideAsync(mode, cancellationToken).ConfigureAwait(false);
                overrideActive = true;
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(settings.PerformanceMatrixSettleSeconds, 1, 20)), cancellationToken)
                    .ConfigureAwait(false);

                result.Runs.Add(await RunXBondMatrixRunAsync(name, mode, cancellationToken).ConfigureAwait(false));
            }

            result.Runs.Add(await RunPublicMatrixRunAsync(cancellationToken).ConfigureAwait(false));
            result.After = await CaptureSystemSampleAsync(cancellationToken).ConfigureAwait(false);
            result.BottleneckSummary = AnalyzeBottleneck(result);
            result.Message = result.Succeeded
                ? "XBond performance matrix completed."
                : "XBond performance matrix completed without successful throughput samples.";
            result.ArtifactPath = await WriteArtifactAsync("performance-matrix", result, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "XBond performance matrix failed");
            result.Error = ex.Message;
            result.Message = "XBond performance matrix failed.";
        }
        finally
        {
            if (overrideActive)
            {
                await ClearDiagnosticOverrideAsync(result, cancellationToken).ConfigureAwait(false);
            }

            result.CompletedAtUtc = DateTime.UtcNow;
            _testLock.Release();
        }

        return result;
    }

    private async Task SetDiagnosticOverrideAsync(string mode, CancellationToken cancellationToken)
    {
        var ttlSeconds = Math.Clamp(settings.PerformanceMatrixOverrideTtlSeconds, 10, 3600)
            .ToString(CultureInfo.InvariantCulture);
        var command = await RunXBondClientCommandAsync(
            [
                "override",
                "set",
                "--mode",
                mode,
                "--policy",
                "diagnostic",
                "--ttl-seconds",
                ttlSeconds,
                "--socket",
                settings.ClientControlSocketPath,
                "--json"
            ],
            timeoutSeconds: Math.Min(settings.ServiceCommandTimeoutSeconds, 15),
            cancellationToken).ConfigureAwait(false);

        if (command.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(command.Output)
                ? $"Failed to set XBond diagnostic override {mode}."
                : command.Output);
        }
    }

    private async Task ClearDiagnosticOverrideAsync(
        XBondPerformanceMatrixResult result,
        CancellationToken cancellationToken)
    {
        try
        {
            var command = await RunXBondClientCommandAsync(
                [
                    "override",
                    "clear",
                    "--socket",
                    settings.ClientControlSocketPath,
                    "--json"
                ],
                timeoutSeconds: Math.Min(settings.ServiceCommandTimeoutSeconds, 15),
                cancellationToken).ConfigureAwait(false);

            if (command.ExitCode != 0)
            {
                var error = string.IsNullOrWhiteSpace(command.Output)
                    ? "Failed to clear XBond diagnostic override."
                    : command.Output;
                result.Error = string.IsNullOrWhiteSpace(result.Error)
                    ? error
                    : $"{result.Error} Clear override failed: {error}";
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to clear XBond diagnostic override");
            result.Error = string.IsNullOrWhiteSpace(result.Error)
                ? $"Clear override failed: {ex.Message}"
                : $"{result.Error} Clear override failed: {ex.Message}";
        }
    }

    public async Task<XBondMtuSweepResult> RunMtuSweepAsync(CancellationToken cancellationToken = default)
    {
        var result = new XBondMtuSweepResult
        {
            StartedAtUtc = DateTime.UtcNow
        };

        if (!OperatingSystem.IsLinux())
        {
            result.Error = "XBond MTU sweep is only available on Linux.";
            result.Message = result.Error;
            result.CompletedAtUtc = DateTime.UtcNow;
            return result;
        }

        if (!await _testLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            result.Error = "Another XBond diagnostic is already running.";
            result.Message = result.Error;
            result.CompletedAtUtc = DateTime.UtcNow;
            return result;
        }

        try
        {
            var status = await statusService.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            result.CurrentMtu = status.Tunnel.Mtu;
            result.MssClampEnabled = settings.MssClampEnabled;
            var values = settings.MtuSweepValues.Length == 0
                ? new[] { 1200, 1280, 1360, 1400, 1420 }
                : settings.MtuSweepValues;

            foreach (var mtu in values.Distinct().OrderBy(value => value))
            {
                var payload = Math.Max(64, mtu - 28);
                var command = await RunCommandAsync(
                    settings.PingCommandPath,
                    ["-4", "-M", "do", "-s", payload.ToString(CultureInfo.InvariantCulture), "-c", "2", "-W", "2", settings.ServerSpeedTestHost],
                    timeoutSeconds: 10,
                    cancellationToken).ConfigureAwait(false);

                result.Probes.Add(new XBondMtuSweepProbe
                {
                    Mtu = mtu,
                    PayloadSize = payload,
                    Succeeded = command.ExitCode == 0,
                    Message = string.IsNullOrWhiteSpace(command.Output)
                        ? $"ping exited with {command.ExitCode}"
                        : command.Output.Split('\n').FirstOrDefault() ?? ""
                });
            }

            result.RecommendedMtu = result.Probes
                .Where(probe => probe.Succeeded)
                .Select(probe => (int?)probe.Mtu)
                .Max();
            result.RecommendedMss = result.RecommendedMtu.HasValue
                ? Math.Max(536, result.RecommendedMtu.Value - 40)
                : null;
            result.Message = result.RecommendedMtu.HasValue
                ? $"MTU sweep completed. Recommended MTU {result.RecommendedMtu}, MSS {result.RecommendedMss}."
                : "MTU sweep completed, but no tested MTU passed DF probing.";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "XBond MTU sweep failed");
            result.Error = ex.Message;
            result.Message = "XBond MTU sweep failed.";
        }
        finally
        {
            result.CompletedAtUtc = DateTime.UtcNow;
            _testLock.Release();
        }

        return result;
    }

    public static XBondSpeedTestResult ParseSpeedTestJson(string output)
    {
        var result = new XBondSpeedTestResult();
        ApplySpeedTestJson(result, output);
        return result;
    }

    public static void ApplySpeedTestJson(XBondSpeedTestResult result, string output)
    {
        var json = ExtractJsonObject(output);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        result.PublicDownloadMbps = TryGetDouble(root, "download") is { } downloadBps
            ? downloadBps / 1_000_000d
            : null;
        result.PublicUploadMbps = TryGetDouble(root, "upload") is { } uploadBps
            ? uploadBps / 1_000_000d
            : null;
        result.PublicPingMs = TryGetDouble(root, "ping");

        if (root.TryGetProperty("server", out var server))
        {
            var sponsor = TryGetString(server, "sponsor");
            var name = TryGetString(server, "name");
            var country = TryGetString(server, "country");
            result.PublicServerName = string.Join(" ", new[] { sponsor, name }.Where(value => !string.IsNullOrWhiteSpace(value)));
            result.PublicServerLocation = string.Join(", ", new[] { name, country }.Where(value => !string.IsNullOrWhiteSpace(value)));
        }

        if (root.TryGetProperty("client", out var client))
        {
            result.ClientIsp = TryGetString(client, "isp") ?? "";
            result.ClientIp = TryGetString(client, "ip") ?? "";
        }
    }

    public static double? ParseIperfBitsPerSecond(string output, string sumProperty)
    {
        var json = ExtractJsonObject(output);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (root.TryGetProperty("error", out var error))
        {
            throw new JsonException(error.GetString() ?? "iperf3 returned an error.");
        }

        if (!root.TryGetProperty("end", out var end) ||
            !end.TryGetProperty(sumProperty, out var sum) ||
            TryGetDouble(sum, "bits_per_second") is not { } bitsPerSecond)
        {
            return null;
        }

        return bitsPerSecond;
    }

    public static ulong? ParseIperfRetransmits(string output, string sumProperty)
    {
        var json = ExtractJsonObject(output);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (!root.TryGetProperty("end", out var end) ||
            !end.TryGetProperty(sumProperty, out var sum) ||
            !sum.TryGetProperty("retransmits", out var retransmits))
        {
            return null;
        }

        return retransmits.ValueKind switch
        {
            JsonValueKind.Number when retransmits.TryGetUInt64(out var number) => number,
            JsonValueKind.String when ulong.TryParse(retransmits.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
            _ => null
        };
    }

    private async Task<XBondPerformanceMatrixRun> RunNativeAdapterMatrixRunAsync(
        XBondPathStatus path,
        CancellationToken cancellationToken)
    {
        var run = new XBondPerformanceMatrixRun
        {
            Name = $"Native {path.Name}",
            Kind = "native-adapter",
            InterfaceName = path.InterfaceName,
            Anchor = path.Role,
            Policy = "native",
            MssClampEnabled = settings.MssClampEnabled
        };

        if (string.IsNullOrWhiteSpace(path.InterfaceName))
        {
            run.Error = "Adapter has no interface name.";
            return run;
        }

        await RunIperfPairIntoRunAsync(
            run,
            string.IsNullOrWhiteSpace(settings.NativeSpeedTestHost) ? settings.PublicTestServerAddress.Split(':')[0] : settings.NativeSpeedTestHost,
            settings.NativeSpeedTestPort > 0 ? settings.NativeSpeedTestPort : settings.ServerSpeedTestPort,
            path.InterfaceName,
            isNativeAdapter: true,
            cancellationToken).ConfigureAwait(false);
        return run;
    }

    private async Task<XBondPerformanceMatrixRun> RunXBondMatrixRunAsync(
        string name,
        string mode,
        CancellationToken cancellationToken)
    {
        var before = await statusService.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var cpuBefore = CaptureProcCpuSample();
        var started = DateTime.UtcNow;
        var run = new XBondPerformanceMatrixRun
        {
            Name = name,
            Kind = "xbond-local",
            Mode = mode,
            Policy = before.RedundancyPolicy,
            Anchor = ResolvePathName(before, before.Schedule.AnchorPathId),
            Backup = string.Join(", ", before.Schedule.DuplicatePathIds.Concat(before.Schedule.FecPathIds).Select(id => ResolvePathName(before, id))),
            ProbePaths = string.Join(", ", before.Paths.Where(path => string.Equals(path.Role, "probe", StringComparison.OrdinalIgnoreCase)).Select(path => path.Name)),
            Mtu = before.Tunnel.Mtu,
            MssClampEnabled = settings.MssClampEnabled
        };

        await RunIperfPairIntoRunAsync(
            run,
            settings.ServerSpeedTestHost,
            settings.ServerSpeedTestPort,
            null,
            isNativeAdapter: false,
            cancellationToken).ConfigureAwait(false);

        var after = await statusService.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var elapsedSeconds = Math.Max(0.001, (DateTime.UtcNow - started).TotalSeconds);
        run.PacketRatePps = (ulong)Math.Max(0, (long)(after.DataPacketsSent - before.DataPacketsSent) + (long)(after.DataPacketsReceived - before.DataPacketsReceived)) / (ulong)Math.Ceiling(elapsedSeconds);
        run.ProcessCpuPercent = CalculateProcCpuPercent(cpuBefore, CaptureProcCpuSample());
        run.ProcessRssBytes = after.Process.RssBytes;
        run.RttMs = after.Paths.Where(path => path.Role is "anchor" or "backup" && path.RttMs.HasValue).Select(path => path.RttMs!.Value).DefaultIfEmpty().Average();
        run.LossPercent = after.Paths.Where(path => path.Role is "anchor" or "backup").Select(path => path.LossRate * 100).DefaultIfEmpty().Max();
        run.LatePercent = after.Paths.Where(path => path.Role is "anchor" or "backup").Select(path => path.LateRate * 100).DefaultIfEmpty().Max();
        run.QueueDepth = after.Paths.Where(path => path.Role is "anchor" or "backup").Select(path => path.QueueDepth).DefaultIfEmpty().Max();
        run.Bottleneck = ClassifyRunBottleneck(run, after);
        return run;
    }

    private async Task<XBondPerformanceMatrixRun> RunPublicMatrixRunAsync(CancellationToken cancellationToken)
    {
        var run = new XBondPerformanceMatrixRun
        {
            Name = "Public speedtest through XBond",
            Kind = "public-speedtest",
            MssClampEnabled = settings.MssClampEnabled
        };

        try
        {
            var speedTest = await RunCommandAsync(
                settings.SpeedTestCommandPath,
                [
                    "--secure",
                    "--json",
                    "--timeout",
                    Math.Clamp(settings.SpeedTestHttpTimeoutSeconds, 5, 60).ToString(CultureInfo.InvariantCulture)
                ],
                timeoutSeconds: Math.Clamp(settings.SpeedTestCommandTimeoutSeconds, 30, 600),
                cancellationToken).ConfigureAwait(false);

            if (speedTest.ExitCode != 0)
            {
                run.Error = string.IsNullOrWhiteSpace(speedTest.Output)
                    ? $"{settings.SpeedTestCommandPath} exited with code {speedTest.ExitCode}."
                    : speedTest.Output;
                return run;
            }

            var parsed = ParseSpeedTestJson(speedTest.Output);
            run.DownloadMbps = parsed.PublicDownloadMbps;
            run.UploadMbps = parsed.PublicUploadMbps;
            run.RttMs = parsed.PublicPingMs;
        }
        catch (Exception ex)
        {
            run.Error = $"Public speedtest failed: {ex.Message}";
        }

        return run;
    }

    private async Task RunIperfPairIntoRunAsync(
        XBondPerformanceMatrixRun run,
        string host,
        int port,
        string? bindDevice,
        bool isNativeAdapter,
        CancellationToken cancellationToken)
    {
        var duration = Math.Clamp(settings.ServerSpeedTestDurationSeconds, 3, 60).ToString(CultureInfo.InvariantCulture);
        var portText = port.ToString(CultureInfo.InvariantCulture);
        var timeoutSeconds = isNativeAdapter
            ? Math.Clamp(settings.NativeSpeedTestCommandTimeoutSeconds, 5, 60)
            : Math.Clamp(settings.ServerSpeedTestCommandTimeoutSeconds, 15, 300);
        var connectTimeoutMs = Math.Clamp(settings.IperfConnectTimeoutMs, 500, 15_000).ToString(CultureInfo.InvariantCulture);

        var uploadArgs = new List<string> { "--client", host, "--port", portText, "--time", duration, "--json", "--connect-timeout", connectTimeoutMs };
        if (!string.IsNullOrWhiteSpace(bindDevice))
        {
            uploadArgs.AddRange(["--bind-dev", bindDevice]);
        }

        try
        {
            var upload = await RunCommandAsync(settings.IperfCommandPath, uploadArgs, timeoutSeconds, cancellationToken).ConfigureAwait(false);
            if (upload.ExitCode != 0)
            {
                run.Error = string.IsNullOrWhiteSpace(upload.Output)
                    ? $"{settings.IperfCommandPath} upload exited with code {upload.ExitCode}."
                    : SummarizeIperfFailureDetails(upload.Output);
                if (isNativeAdapter)
                {
                    run.Error = FormatNativeIperfFailure(
                        "Upload",
                        bindDevice,
                        host,
                        port,
                        settings.ServerSpeedTestHost,
                        settings.ServerSpeedTestPort,
                        run.Error);
                }

                return;
            }

            run.UploadMbps = ParseIperfBitsPerSecond(upload.Output, "sum_sent") / 1_000_000d;
            run.Retransmits = ParseIperfRetransmits(upload.Output, "sum_sent");
        }
        catch (Exception ex)
        {
            run.Error = isNativeAdapter
                ? FormatNativeIperfFailure(
                    "Upload",
                    bindDevice,
                    host,
                    port,
                    settings.ServerSpeedTestHost,
                    settings.ServerSpeedTestPort,
                    ex.Message)
                : $"Upload failed: {ex.Message}";
            return;
        }

        var downloadArgs = new List<string> { "--client", host, "--port", portText, "--time", duration, "--reverse", "--json", "--connect-timeout", connectTimeoutMs };
        if (!string.IsNullOrWhiteSpace(bindDevice))
        {
            downloadArgs.AddRange(["--bind-dev", bindDevice]);
        }

        try
        {
            var download = await RunCommandAsync(settings.IperfCommandPath, downloadArgs, timeoutSeconds, cancellationToken).ConfigureAwait(false);
            if (download.ExitCode != 0)
            {
                run.Error = string.IsNullOrWhiteSpace(download.Output)
                    ? $"{settings.IperfCommandPath} download exited with code {download.ExitCode}."
                    : SummarizeIperfFailureDetails(download.Output);
                if (isNativeAdapter)
                {
                    run.Error = FormatNativeIperfFailure(
                        "Download",
                        bindDevice,
                        host,
                        port,
                        settings.ServerSpeedTestHost,
                        settings.ServerSpeedTestPort,
                        run.Error);
                }

                return;
            }

            run.DownloadMbps = ParseIperfBitsPerSecond(download.Output, "sum_received") / 1_000_000d;
        }
        catch (Exception ex)
        {
            run.Error = isNativeAdapter
                ? FormatNativeIperfFailure(
                    "Download",
                    bindDevice,
                    host,
                    port,
                    settings.ServerSpeedTestHost,
                    settings.ServerSpeedTestPort,
                    ex.Message)
                : $"Download failed: {ex.Message}";
        }
    }

    public static string FormatNativeIperfFailure(
        string phase,
        string? interfaceName,
        string nativeHost,
        int nativePort,
        string tunnelHost,
        int tunnelPort,
        string details)
    {
        var adapter = string.IsNullOrWhiteSpace(interfaceName) ? "adapter" : $"adapter {interfaceName}";
        var message = $"{phase} failed for native {adapter}: {details}";
        return $"{message}. Native per-adapter iperf requires an iperf3 listener reachable outside XBond at {nativeHost}:{nativePort}; the standard XBond iperf endpoint is tunnel-only at {tunnelHost}:{tunnelPort}.";
    }

    public static string SummarizeIperfFailureDetails(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return "iperf3 returned no error details.";
        }

        try
        {
            var json = ExtractJsonObject(output);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(error.GetString()))
            {
                return error.GetString()!;
            }
        }
        catch (JsonException)
        {
        }

        var compact = string.Join(
            " ",
            output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0));
        return compact.Length <= 240 ? compact : $"{compact[..237]}...";
    }

    private async Task<XBondMatrixSystemSample> CaptureSystemSampleAsync(CancellationToken cancellationToken)
    {
        var status = await statusService.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        return new XBondMatrixSystemSample
        {
            SampledAtUtc = DateTime.UtcNow,
            DataPacketsSent = status.DataPacketsSent,
            DataPacketsReceived = status.DataPacketsReceived,
            DuplicatePacketsSent = status.DuplicatePacketsSent,
            LatePacketsDropped = status.LatePacketsDropped,
            ReorderPendingDepth = status.Reorder.ReturnPath.PendingDepth,
            ReorderHeldPackets = status.Reorder.ReturnPath.HeldPackets,
            ReorderReleasedGapPackets = status.Reorder.ReturnPath.ReleasedGapPackets,
            ReorderLateDuplicates = status.Reorder.ReturnPath.LateDuplicates,
            ProcessRssBytes = status.Process.RssBytes
        };
    }

    private static string ResolvePathName(XBondStatus status, int? pathId)
    {
        if (!pathId.HasValue)
        {
            return "";
        }

        var path = status.Paths.FirstOrDefault(item => item.PathId == pathId.Value);
        return path is null ? $"Path {pathId}" : path.Name;
    }

    private static string AnalyzeBottleneck(XBondPerformanceMatrixResult result)
    {
        var successful = result.Runs.Where(run => run.Succeeded).ToList();
        if (successful.Count == 0)
        {
            return "unknown";
        }

        if (successful.Any(run => run.ProcessCpuPercent >= 85))
        {
            return "CPU bound";
        }

        if (successful.Any(run => run.LossPercent >= 5))
        {
            return "path loss";
        }

        var anchorOnly = successful.FirstOrDefault(run => string.Equals(run.Mode, "anchor-only", StringComparison.OrdinalIgnoreCase));
        var duplicate = successful.FirstOrDefault(run => string.Equals(run.Mode, "anchor-duplicate-1", StringComparison.OrdinalIgnoreCase));
        if (anchorOnly is not null &&
            duplicate is not null &&
            anchorOnly.DownloadMbps.HasValue &&
            duplicate.DownloadMbps.HasValue &&
            duplicate.DownloadMbps.Value < anchorOnly.DownloadMbps.Value * 0.75)
        {
            return "backup harming anchor";
        }

        if ((result.After.ReorderReleasedGapPackets ?? 0) > (result.Before.ReorderReleasedGapPackets ?? 0) ||
            (result.After.ReorderLateDuplicates ?? 0) > (result.Before.ReorderLateDuplicates ?? 0))
        {
            return "packet reordering";
        }

        if (successful.Any(run => run.Mtu is <= 1280))
        {
            return "MTU/fragmentation";
        }

        if (successful.Any(run => string.Equals(run.Bottleneck, "scheduler demotion", StringComparison.OrdinalIgnoreCase)))
        {
            return "scheduler demotion";
        }

        return "unknown";
    }

    private static string ClassifyRunBottleneck(XBondPerformanceMatrixRun run, XBondStatus status)
    {
        if (run.ProcessCpuPercent >= 85)
        {
            return "CPU bound";
        }

        if (run.LossPercent >= 5)
        {
            return "path loss";
        }

        if (status.Paths.Any(path => !string.IsNullOrWhiteSpace(path.DemotionReason)))
        {
            return "scheduler demotion";
        }

        if (status.Reorder.ReturnPath.ReleasedGapPackets > 0 || status.Reorder.ReturnPath.LateDuplicates > 0)
        {
            return "packet reordering";
        }

        return "unknown";
    }

    private async Task<string?> WriteArtifactAsync(
        string prefix,
        object result,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(result, ArtifactJsonOptions);
        var errors = new List<string>();

        foreach (var directory in ResolveArtifactDirectoryCandidates(settings.PerformanceArtifactDirectory))
        {
            try
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, $"{prefix}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
                await File.WriteAllTextAsync(path, json, cancellationToken).ConfigureAwait(false);
                return path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{directory}: {ex.Message}");
            }
        }

        if (errors.Count == 0)
        {
            return null;
        }

        throw new IOException($"Unable to write XBond diagnostic artifact. {string.Join(" ", errors)}");
    }

    public static IReadOnlyList<string> ResolveArtifactDirectoryCandidates(string? configuredDirectory)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configuredDirectory))
        {
            candidates.Add(configuredDirectory);
        }

        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localApplicationData))
        {
            candidates.Add(Path.Combine(localApplicationData, "XNetwork", "diagnostics"));
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            candidates.Add(Path.Combine(userProfile, ".local", "state", "xnetwork", "diagnostics"));
        }

        candidates.Add(Path.Combine(Path.GetTempPath(), "xnetwork-diagnostics"));

        return candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private Task<CommandResult> RunXBondClientCommandAsync(
        IReadOnlyList<string> arguments,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        if (settings.UseSudoForServiceManager && !string.IsNullOrWhiteSpace(settings.SudoPath))
        {
            return RunCommandAsync(
                settings.SudoPath,
                ["-n", settings.ClientBinaryPath, .. arguments],
                timeoutSeconds,
                cancellationToken);
        }

        return RunCommandAsync(settings.ClientBinaryPath, arguments, timeoutSeconds, cancellationToken);
    }

    private static ProcCpuSample? CaptureProcCpuSample()
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        try
        {
            var pid = File.ReadAllText("/run/xbond/client.pid").Trim();
            if (string.IsNullOrWhiteSpace(pid))
            {
                return null;
            }

            return new ProcCpuSample(ReadTotalJiffies(), ReadProcessJiffies(pid));
        }
        catch
        {
            try
            {
                var pid = RunCommandForText("pidof", ["xbond-client"]).Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                return string.IsNullOrWhiteSpace(pid)
                    ? null
                    : new ProcCpuSample(ReadTotalJiffies(), ReadProcessJiffies(pid));
            }
            catch
            {
                return null;
            }
        }
    }

    private static double? CalculateProcCpuPercent(ProcCpuSample? before, ProcCpuSample? after)
    {
        if (before is null || after is null)
        {
            return null;
        }

        var totalDelta = after.TotalJiffies - before.TotalJiffies;
        var processDelta = after.ProcessJiffies - before.ProcessJiffies;
        if (totalDelta <= 0 || processDelta < 0)
        {
            return null;
        }

        return Math.Round(processDelta * Environment.ProcessorCount * 100d / totalDelta, 1);
    }

    private static long ReadTotalJiffies()
    {
        var parts = File.ReadLines("/proc/stat").First().Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1);
        return parts.Select(value => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0).Sum();
    }

    private static long ReadProcessJiffies(string pid)
    {
        var stat = File.ReadAllText($"/proc/{pid}/stat");
        var closeParen = stat.LastIndexOf(')');
        var fields = stat[(closeParen + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var utime = long.Parse(fields[11], CultureInfo.InvariantCulture);
        var stime = long.Parse(fields[12], CultureInfo.InvariantCulture);
        return utime + stime;
    }

    private static string RunCommandForText(string command, IReadOnlyList<string> arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = command,
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

        process.Start();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(2_000);
        return output;
    }

    private sealed record ProcCpuSample(long TotalJiffies, long ProcessJiffies);

    private async Task RunServerSpeedTestAsync(
        XBondSpeedTestResult result,
        CancellationToken cancellationToken)
    {
        var duration = Math.Clamp(settings.ServerSpeedTestDurationSeconds, 3, 60).ToString(CultureInfo.InvariantCulture);
        var port = settings.ServerSpeedTestPort.ToString(CultureInfo.InvariantCulture);
        var timeoutSeconds = Math.Clamp(settings.ServerSpeedTestCommandTimeoutSeconds, 15, 300);

        try
        {
            var upload = await RunCommandAsync(
                settings.IperfCommandPath,
                ["--client", settings.ServerSpeedTestHost, "--port", port, "--time", duration, "--json"],
                timeoutSeconds,
                cancellationToken).ConfigureAwait(false);

            if (upload.ExitCode != 0)
            {
                result.ServerTestError = string.IsNullOrWhiteSpace(upload.Output)
                    ? $"{settings.IperfCommandPath} upload exited with code {upload.ExitCode}."
                    : upload.Output;
                return;
            }

            result.ServerUploadMbps = ParseIperfBitsPerSecond(upload.Output, "sum_sent") / 1_000_000d;
        }
        catch (Exception ex)
        {
            result.ServerTestError = $"Pi to Vultr upload failed: {ex.Message}";
            return;
        }

        try
        {
            var download = await RunCommandAsync(
                settings.IperfCommandPath,
                ["--client", settings.ServerSpeedTestHost, "--port", port, "--time", duration, "--reverse", "--json"],
                timeoutSeconds,
                cancellationToken).ConfigureAwait(false);

            if (download.ExitCode != 0)
            {
                result.ServerTestError = string.IsNullOrWhiteSpace(download.Output)
                    ? $"{settings.IperfCommandPath} download exited with code {download.ExitCode}."
                    : download.Output;
                return;
            }

            result.ServerDownloadMbps = ParseIperfBitsPerSecond(download.Output, "sum_received") / 1_000_000d;
        }
        catch (Exception ex)
        {
            result.ServerTestError = $"Vultr to Pi download failed: {ex.Message}";
        }
    }

    private async Task RunPublicSpeedTestAsync(
        XBondSpeedTestResult result,
        CancellationToken cancellationToken)
    {
        try
        {
            var speedTest = await RunCommandAsync(
                settings.SpeedTestCommandPath,
                [
                    "--secure",
                    "--json",
                    "--timeout",
                    Math.Clamp(settings.SpeedTestHttpTimeoutSeconds, 5, 60).ToString()
                ],
                timeoutSeconds: Math.Clamp(settings.SpeedTestCommandTimeoutSeconds, 30, 600),
                cancellationToken).ConfigureAwait(false);

            if (speedTest.ExitCode != 0)
            {
                result.PublicTestError = string.IsNullOrWhiteSpace(speedTest.Output)
                    ? $"{settings.SpeedTestCommandPath} exited with code {speedTest.ExitCode}."
                    : speedTest.Output;
                return;
            }

            ApplySpeedTestJson(result, speedTest.Output);
        }
        catch (Exception ex)
        {
            result.PublicTestError = $"Public speedtest failed: {ex.Message}";
        }
    }

    private static string ExtractJsonObject(string output)
    {
        var start = output.IndexOf('{');
        var end = output.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            throw new JsonException("Speed test did not return JSON output.");
        }

        return output[start..(end + 1)];
    }

    private static double? TryGetDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var number) => number,
            JsonValueKind.String when double.TryParse(value.GetString(), CultureInfo.InvariantCulture, out var number) => number,
            _ => null
        };
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static async Task<CommandResult> RunCommandAsync(
        string command,
        IReadOnlyList<string> arguments,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = command,
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
            // Best effort cleanup for a timed-out speed test.
        }
    }

    private sealed record CommandResult(int ExitCode, string Output);
}

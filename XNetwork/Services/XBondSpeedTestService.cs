using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using XNetwork.Models;

namespace XNetwork.Services;

public sealed class XBondSpeedTestService(
    ILogger<XBondSpeedTestService> logger,
    XBondSettings settings)
{
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

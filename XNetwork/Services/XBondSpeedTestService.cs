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
            var route = await RunCommandAsync(
                settings.RouteCommandPath,
                ["route", "get", "1.1.1.1"],
                timeoutSeconds: Math.Min(settings.ScopedRouteCommandTimeoutSeconds, 15),
                cancellationToken).ConfigureAwait(false);

            result.RouteOutput = route.Output;
            result.RouteUsesXBond = route.ExitCode == 0 &&
                                    route.Output.Contains($"dev {settings.TunnelDevice}", StringComparison.Ordinal);

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
                result.Error = string.IsNullOrWhiteSpace(speedTest.Output)
                    ? $"{settings.SpeedTestCommandPath} exited with code {speedTest.ExitCode}."
                    : speedTest.Output;
                result.Message = "XBond speed test failed.";
                return result;
            }

            ApplySpeedTestJson(result, speedTest.Output);
            result.Message = result.RouteUsesXBond
                ? "XBond speed test completed."
                : $"Speed test completed, but the default route was not using {settings.TunnelDevice}.";
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

        result.DownloadMbps = TryGetDouble(root, "download") is { } downloadBps
            ? downloadBps / 1_000_000d
            : null;
        result.UploadMbps = TryGetDouble(root, "upload") is { } uploadBps
            ? uploadBps / 1_000_000d
            : null;
        result.PingMs = TryGetDouble(root, "ping");

        if (root.TryGetProperty("server", out var server))
        {
            var sponsor = TryGetString(server, "sponsor");
            var name = TryGetString(server, "name");
            var country = TryGetString(server, "country");
            result.ServerName = string.Join(" ", new[] { sponsor, name }.Where(value => !string.IsNullOrWhiteSpace(value)));
            result.ServerLocation = string.Join(", ", new[] { name, country }.Where(value => !string.IsNullOrWhiteSpace(value)));
        }

        if (root.TryGetProperty("client", out var client))
        {
            result.ClientIsp = TryGetString(client, "isp") ?? "";
            result.ClientIp = TryGetString(client, "ip") ?? "";
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

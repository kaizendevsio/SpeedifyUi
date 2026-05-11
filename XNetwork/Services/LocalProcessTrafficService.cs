using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using XNetwork.Models;

namespace XNetwork.Services;

public class LocalProcessTrafficService(ILogger<LocalProcessTrafficService> logger)
{
    private const string DefaultInterface = "connectify0";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(4);
    private static readonly Regex NethogsLineRegex = new("^(?<command>.+?)\\s+(?<upload>\\d+(?:\\.\\d+)?)\\s+(?<download>\\d+(?:\\.\\d+)?)$", RegexOptions.Compiled);
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private LocalProcessTrafficSnapshot? _cachedSnapshot;
    private long _cachedAtTicks;

    public async Task<LocalProcessTrafficSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux())
        {
            return LocalProcessTrafficSnapshot.Unsupported("Local process traffic attribution is only supported on Linux routers.");
        }

        var nowTicks = DateTime.UtcNow.Ticks;
        if (_cachedSnapshot is not null && nowTicks - Volatile.Read(ref _cachedAtTicks) < CacheDuration.Ticks)
        {
            return _cachedSnapshot;
        }

        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            nowTicks = DateTime.UtcNow.Ticks;
            if (_cachedSnapshot is not null && nowTicks - Volatile.Read(ref _cachedAtTicks) < CacheDuration.Ticks)
            {
                return _cachedSnapshot;
            }

            var snapshot = await SampleWithNethogsAsync(cancellationToken).ConfigureAwait(false);
            _cachedSnapshot = snapshot;
            Volatile.Write(ref _cachedAtTicks, nowTicks);
            return snapshot;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<LocalProcessTrafficSnapshot> SampleWithNethogsAsync(CancellationToken cancellationToken)
    {
        if (!await CommandExistsAsync("nethogs", cancellationToken).ConfigureAwait(false))
        {
            return LocalProcessTrafficSnapshot.Unsupported("Install nethogs and allow xnetwork to run it for live per-process throughput.");
        }

        try
        {
            var result = await RunProcessAsync("nethogs", ["-t", "-b", "-C", "-d", "1", "-c", "1", DefaultInterface], cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                var message = string.IsNullOrWhiteSpace(result.Error)
                    ? "nethogs could not sample local process traffic. It usually needs root/CAP_NET_ADMIN."
                    : result.Error.Trim();

                return LocalProcessTrafficSnapshot.Unsupported(message);
            }

            var processes = ParseNethogsOutput(result.Output)
                .OrderByDescending(process => process.TotalMbps)
                .Take(8)
                .ToList();

            return new LocalProcessTrafficSnapshot
            {
                IsSupported = true,
                Source = $"nethogs on {DefaultInterface}",
                Message = processes.Count == 0 ? "No local process traffic was observed in this sample." : "Live local process throughput sampled from the Speedify tunnel interface.",
                Processes = processes
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not sample local process traffic with nethogs");
            return LocalProcessTrafficSnapshot.Unsupported($"Could not sample local process traffic: {ex.Message}");
        }
    }

    public static IReadOnlyList<LocalProcessTrafficItem> ParseNethogsOutput(string output)
    {
        var latestByCommand = new Dictionary<string, LocalProcessTrafficItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 ||
                line.StartsWith("Refreshing", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("total", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var match = NethogsLineRegex.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var command = match.Groups["command"].Value.Trim();
            if (!double.TryParse(match.Groups["upload"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var uploadKbps) ||
                !double.TryParse(match.Groups["download"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var downloadKbps))
            {
                continue;
            }

            if (uploadKbps <= 0 && downloadKbps <= 0)
            {
                continue;
            }

            latestByCommand[command] = new LocalProcessTrafficItem
            {
                Command = command,
                ProcessName = ExtractProcessName(command),
                ProcessId = ExtractProcessId(command),
                UploadMbps = KilobytesPerSecondToMegabitsPerSecond(uploadKbps),
                DownloadMbps = KilobytesPerSecondToMegabitsPerSecond(downloadKbps)
            };
        }

        return latestByCommand.Values.ToList();
    }

    private static double KilobytesPerSecondToMegabitsPerSecond(double kilobytesPerSecond)
    {
        return kilobytesPerSecond * 8.0 / 1000.0;
    }

    private static string ExtractProcessName(string command)
    {
        if (command.StartsWith("unknown ", StringComparison.OrdinalIgnoreCase))
        {
            return "Unknown tunnel flow";
        }

        var withoutMetadata = Regex.Replace(command, "/\\d+(?:/\\d+)?$", "");
        var name = withoutMetadata.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return string.IsNullOrWhiteSpace(name) ? command : name;
    }

    private static int? ExtractProcessId(string command)
    {
        var match = Regex.Match(command, "/(?<pid>\\d+)(?:/\\d+)?$");
        return match.Success && int.TryParse(match.Groups["pid"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid) && pid > 0
            ? pid
            : null;
    }

    private static async Task<bool> CommandExistsAsync(string command, CancellationToken cancellationToken)
    {
        var result = await RunProcessAsync("/bin/sh", ["-c", $"command -v {command}"], cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Output);
    }

    private static async Task<ProcessResult> RunProcessAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(CommandTimeout);

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

        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var errorTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            return new ProcessResult(process.ExitCode, output, error);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            return new ProcessResult(124, "", $"{fileName} timed out while sampling local process traffic.");
        }
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}

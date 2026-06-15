using System.Diagnostics;

namespace XNetwork.Services;

public sealed class InterfaceMetadataService(ILogger<InterfaceMetadataService> logger)
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private IReadOnlyDictionary<string, string> _displayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private DateTime _expiresAtUtc = DateTime.MinValue;

    public async Task<IReadOnlyDictionary<string, string>> GetDisplayNamesAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux())
        {
            return _displayNames;
        }

        var now = DateTime.UtcNow;
        if (now < _expiresAtUtc)
        {
            return _displayNames;
        }

        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = DateTime.UtcNow;
            if (now < _expiresAtUtc)
            {
                return _displayNames;
            }

            _displayNames = await ReadNetworkManagerNamesAsync(cancellationToken).ConfigureAwait(false);
            _expiresAtUtc = now + CacheDuration;
            return _displayNames;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<IReadOnlyDictionary<string, string>> ReadNetworkManagerNamesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var commandExists = await RunProcessAsync(
                "/usr/bin/env",
                ["sh", "-c", "command -v nmcli"],
                cancellationToken).ConfigureAwait(false);

            if (commandExists.ExitCode != 0)
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            var result = await RunProcessAsync(
                "nmcli",
                ["-t", "-f", "DEVICE,TYPE,STATE,CONNECTION", "device", "status"],
                cancellationToken).ConfigureAwait(false);

            if (result.ExitCode != 0)
            {
                logger.LogDebug("nmcli device status failed: {Error}", result.StandardError.Trim());
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            return ParseNmcliDeviceStatus(result.StandardOutput);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Unable to refresh interface display names from NetworkManager");
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public static IReadOnlyDictionary<string, string> ParseNmcliDeviceStatus(string output)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = ParseTerseLine(rawLine);
            if (parts.Count < 4)
            {
                continue;
            }

            var device = parts[0].Trim();
            var connection = parts[3].Trim();
            if (string.IsNullOrWhiteSpace(device) || IsGenericConnectionName(connection, device))
            {
                continue;
            }

            names[device] = connection;
        }

        return names;
    }

    private static bool IsGenericConnectionName(string value, string device)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "--")
        {
            return true;
        }

        if (string.Equals(value, device, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return value.StartsWith("Wired connection ", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("lo", StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> ParseTerseLine(string line)
    {
        var parts = new List<string>();
        var current = new List<char>();
        var escaped = false;

        foreach (var ch in line)
        {
            if (escaped)
            {
                current.Add(ch);
                escaped = false;
                continue;
            }

            if (ch == '\\')
            {
                escaped = true;
                continue;
            }

            if (ch == ':')
            {
                parts.Add(new string(current.ToArray()));
                current.Clear();
                continue;
            }

            current.Add(ch);
        }

        parts.Add(new string(current.ToArray()));
        return parts;
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in args)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return new ProcessResult(
            process.ExitCode,
            await outputTask.ConfigureAwait(false),
            await errorTask.ConfigureAwait(false));
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}

using System.Diagnostics;
using XNetwork.Models;

namespace XNetwork.Services;

public class WifiService(ILogger<WifiService> logger)
{
    public async Task<WifiConnectionStatus> GetStatusAsync(string interfaceName = "wlan0", CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux())
        {
            return new WifiConnectionStatus { IsSupported = false, Message = "Wi-Fi control is only supported on Linux." };
        }

        if (!await CommandExistsAsync("nmcli", cancellationToken).ConfigureAwait(false))
        {
            return new WifiConnectionStatus { IsSupported = false, Message = "NetworkManager nmcli was not found on this router." };
        }

        var deviceOutput = await RunNmcliAsync(new[] { "-t", "-f", "DEVICE,TYPE,STATE,CONNECTION", "device", "status" }, cancellationToken).ConfigureAwait(false);
        var wifiDevices = deviceOutput.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseTerseLine)
            .Where(parts => parts.Count >= 4 && parts[1] == "wifi" && !parts[0].StartsWith("p2p-", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var selected = wifiDevices.FirstOrDefault(parts => string.Equals(parts[0], interfaceName, StringComparison.OrdinalIgnoreCase)) ?? wifiDevices.FirstOrDefault();
        if (selected == null)
        {
            return new WifiConnectionStatus { IsSupported = false, InterfaceName = interfaceName, Message = "No on-board Wi-Fi interface was found." };
        }

        var status = new WifiConnectionStatus
        {
            IsSupported = true,
            InterfaceName = selected[0],
            State = selected[2],
            ConnectionName = string.IsNullOrWhiteSpace(selected[3]) ? null : selected[3]
        };

        try
        {
            var wifiOutput = await RunNmcliAsync(new[] { "-t", "-f", "ACTIVE,SSID,SIGNAL,SECURITY", "device", "wifi", "list", "ifname", status.InterfaceName, "--rescan", "no" }, cancellationToken).ConfigureAwait(false);
            var active = wifiOutput.Output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(ParseTerseLine)
                .FirstOrDefault(parts => parts.Count >= 4 && string.Equals(parts[0], "yes", StringComparison.OrdinalIgnoreCase));

            if (active != null)
            {
                status.Ssid = active[1];
                status.Signal = int.TryParse(active[2], out var signal) ? signal : null;
                status.Security = active[3];
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not read active Wi-Fi network details");
        }

        return status;
    }

    public async Task<WifiConnectionStatus> ConnectAsync(string interfaceName, string ssid, string password, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Wi-Fi control is only supported on Linux.");
        }

        if (string.IsNullOrWhiteSpace(ssid))
        {
            throw new ArgumentException("Wi-Fi name is required.", nameof(ssid));
        }

        interfaceName = string.IsNullOrWhiteSpace(interfaceName) ? "wlan0" : interfaceName.Trim();
        ssid = ssid.Trim();

        var connectionName = $"XNetwork Wi-Fi {ssid}";
        var args = new List<string>
        {
            "--wait", "35",
            "device", "wifi", "connect", ssid,
            "ifname", interfaceName,
            "name", connectionName
        };

        string? standardInput = null;
        if (!string.IsNullOrEmpty(password))
        {
            args.Insert(0, "--ask");
            standardInput = password;
        }

        logger.LogInformation("Connecting Wi-Fi interface {Interface} to SSID {Ssid}", interfaceName, ssid);
        var result = await RunNmcliAsync(args, cancellationToken, standardInput).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error) ? result.Output.Trim() : result.Error.Trim());
        }

        var status = await GetStatusAsync(interfaceName, cancellationToken).ConfigureAwait(false);
        status.Message = result.Output.Trim();
        return status;
    }

    private static async Task<bool> CommandExistsAsync(string command, CancellationToken cancellationToken)
    {
        var result = await RunProcessAsync("/usr/bin/env", new[] { "sh", "-c", $"command -v {command}" }, cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0;
    }

    private static Task<ProcessResult> RunNmcliAsync(IEnumerable<string> args, CancellationToken cancellationToken, string? standardInput = null)
    {
        return RunProcessAsync("nmcli", args, cancellationToken, standardInput);
    }

    private static async Task<ProcessResult> RunProcessAsync(string fileName, IEnumerable<string> args, CancellationToken cancellationToken, string? standardInput = null)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardInput = standardInput != null,
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
        if (standardInput != null)
        {
            await process.StandardInput.WriteLineAsync(standardInput.AsMemory(), cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return new ProcessResult(process.ExitCode, await outputTask.ConfigureAwait(false), await errorTask.ConfigureAwait(false));
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

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}

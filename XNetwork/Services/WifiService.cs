using System.Diagnostics;
using XNetwork.Models;

namespace XNetwork.Services;

public class WifiService(ILogger<WifiService> logger)
{
    public async Task<IReadOnlyList<WifiInterfaceInfo>> GetWifiInterfacesAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux())
        {
            return [];
        }

        if (!await CommandExistsAsync("nmcli", cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        var deviceOutput = await RunNmcliAsync(new[] { "-t", "-f", "DEVICE,TYPE,STATE,CONNECTION", "device", "status" }, cancellationToken).ConfigureAwait(false);
        return ParseWifiInterfaces(deviceOutput.Output);
    }

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

        var wifiDevices = await GetWifiInterfacesAsync(cancellationToken).ConfigureAwait(false);
        var selectedInterface = SelectDefaultInterface(wifiDevices, interfaceName);
        var selected = wifiDevices.FirstOrDefault(device => string.Equals(device.InterfaceName, selectedInterface, StringComparison.OrdinalIgnoreCase));
        if (selected == null)
        {
            return new WifiConnectionStatus { IsSupported = false, InterfaceName = interfaceName, Message = "No Wi-Fi interface was found." };
        }

        var status = new WifiConnectionStatus
        {
            IsSupported = true,
            InterfaceName = selected.InterfaceName,
            State = selected.State,
            ConnectionName = selected.ConnectionName
        };

        try
        {
            var wifiOutput = await RunNmcliAsync(new[] { "-t", "-f", "ACTIVE,BSSID,SSID,SIGNAL,SECURITY", "device", "wifi", "list", "ifname", status.InterfaceName, "--rescan", "no" }, cancellationToken).ConfigureAwait(false);
            var active = wifiOutput.Output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(ParseTerseLine)
                .FirstOrDefault(parts => parts.Count >= 5 && string.Equals(parts[0], "yes", StringComparison.OrdinalIgnoreCase));

            if (active != null)
            {
                status.Bssid = active[1];
                status.Ssid = active[2];
                status.Signal = int.TryParse(active[3], out var signal) ? signal : null;
                status.Security = active[4];
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

    public async Task<List<WifiNetwork>> ScanNetworksAsync(string interfaceName = "wlan0", bool rescan = true, CancellationToken cancellationToken = default, bool collapseBySsid = true)
    {
        if (!OperatingSystem.IsLinux())
        {
            return new List<WifiNetwork>();
        }

        if (!await CommandExistsAsync("nmcli", cancellationToken).ConfigureAwait(false))
        {
            return new List<WifiNetwork>();
        }

        interfaceName = string.IsNullOrWhiteSpace(interfaceName) ? "wlan0" : interfaceName.Trim();
        var output = await RunNmcliAsync(
            new[]
            {
                "-t", "-f", "ACTIVE,BSSID,SSID,SIGNAL,SECURITY",
                "device", "wifi", "list",
                "ifname", interfaceName,
                "--rescan", rescan ? "yes" : "no"
            },
            cancellationToken).ConfigureAwait(false);

        if (output.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(output.Error) ? output.Output.Trim() : output.Error.Trim());
        }

        var networks = output.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseTerseLine)
            .Where(parts => parts.Count >= 5 && !string.IsNullOrWhiteSpace(parts[2]))
            .Select(parts => new WifiNetwork
            {
                IsActive = string.Equals(parts[0], "yes", StringComparison.OrdinalIgnoreCase),
                Bssid = parts[1],
                Ssid = parts[2],
                Signal = int.TryParse(parts[3], out var signal) ? signal : 0,
                Security = parts[4]
            })
            .ToList();

        var orderedNetworks = collapseBySsid
            ? networks
                .GroupBy(network => network.Ssid, StringComparer.Ordinal)
                .Select(group => group.OrderByDescending(network => network.IsActive).ThenByDescending(network => network.Signal).First())
            : networks;

        return orderedNetworks
            .OrderByDescending(network => network.IsActive)
            .ThenByDescending(network => network.Signal)
            .ThenBy(network => network.Ssid)
            .ToList();
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

    public static IReadOnlyList<WifiInterfaceInfo> ParseWifiInterfaces(string output)
    {
        return output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseTerseLine)
            .Where(parts => parts.Count >= 4 && parts[1] == "wifi" && !parts[0].StartsWith("p2p-", StringComparison.OrdinalIgnoreCase))
            .Select(parts => new WifiInterfaceInfo
            {
                InterfaceName = parts[0],
                State = string.IsNullOrWhiteSpace(parts[2]) ? "unknown" : parts[2],
                ConnectionName = string.IsNullOrWhiteSpace(parts[3]) || parts[3] == "--" ? null : parts[3]
            })
            .OrderByDescending(device => device.IsConnected)
            .ThenBy(device => device.InterfaceName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string SelectDefaultInterface(IReadOnlyList<WifiInterfaceInfo> interfaces, string? preferredInterface = null)
    {
        if (!string.IsNullOrWhiteSpace(preferredInterface) &&
            interfaces.Any(device => string.Equals(device.InterfaceName, preferredInterface.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            return preferredInterface.Trim();
        }

        return interfaces.FirstOrDefault(device => device.IsConnected)?.InterfaceName
               ?? interfaces.FirstOrDefault(device => string.Equals(device.InterfaceName, "wlan0", StringComparison.OrdinalIgnoreCase))?.InterfaceName
               ?? interfaces.FirstOrDefault()?.InterfaceName
               ?? (string.IsNullOrWhiteSpace(preferredInterface) ? "wlan0" : preferredInterface.Trim());
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

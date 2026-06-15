using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XNetwork.Services;

public sealed class InterfaceMetadataService(ILogger<InterfaceMetadataService> logger)
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private IReadOnlyList<InterfaceMetadata> _interfaces = [];
    private DateTime _expiresAtUtc = DateTime.MinValue;

    public async Task<IReadOnlyDictionary<string, string>> GetDisplayNamesAsync(CancellationToken cancellationToken = default)
    {
        var interfaces = await GetInterfacesAsync(cancellationToken).ConfigureAwait(false);
        return interfaces
            .Where(item => !string.IsNullOrWhiteSpace(item.DisplayName))
            .ToDictionary(item => item.Device, item => item.DisplayName, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyList<InterfaceMetadata>> GetInterfacesAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux())
        {
            return _interfaces;
        }

        var now = DateTime.UtcNow;
        if (now < _expiresAtUtc)
        {
            return _interfaces;
        }

        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = DateTime.UtcNow;
            if (now < _expiresAtUtc)
            {
                return _interfaces;
            }

            _interfaces = await ReadNetworkManagerInterfacesAsync(cancellationToken).ConfigureAwait(false);
            _expiresAtUtc = now + CacheDuration;
            return _interfaces;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<IReadOnlyList<InterfaceMetadata>> ReadNetworkManagerInterfacesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var commandExists = await RunProcessAsync(
                "/usr/bin/env",
                ["sh", "-c", "command -v nmcli"],
                cancellationToken).ConfigureAwait(false);

            if (commandExists.ExitCode != 0)
            {
                return [];
            }

            var result = await RunProcessAsync(
                "nmcli",
                ["-t", "-f", "DEVICE,TYPE,STATE,CONNECTION", "device", "status"],
                cancellationToken).ConfigureAwait(false);

            if (result.ExitCode != 0)
            {
                logger.LogDebug("nmcli device status failed: {Error}", result.StandardError.Trim());
                return [];
            }

            var interfaces = ParseNmcliDeviceMetadata(result.StandardOutput)
                .ToDictionary(item => item.Device, StringComparer.OrdinalIgnoreCase);

            foreach (var (device, provider) in await ReadGatewayProviderNamesAsync(cancellationToken).ConfigureAwait(false))
            {
                if (interfaces.TryGetValue(device, out var metadata))
                {
                    interfaces[device] = metadata with { DisplayName = provider };
                }
            }

            return interfaces.Values
                .OrderBy(item => item.Device, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Unable to refresh interface display names from NetworkManager");
            return [];
        }
    }

    private async Task<IReadOnlyDictionary<string, string>> ReadGatewayProviderNamesAsync(CancellationToken cancellationToken)
    {
        var routesResult = await RunProcessAsync(
            "ip",
            ["-j", "route", "show", "default"],
            cancellationToken).ConfigureAwait(false);

        if (routesResult.ExitCode != 0)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var routes = ParseDefaultRoutes(routesResult.StandardOutput);
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var route in routes)
        {
            if (ShouldSkipGatewayProbe(route.Device, route.Gateway))
            {
                continue;
            }

            string? provider;
            try
            {
                provider = await ReadModemProviderAsync(route.Gateway, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                continue;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Unable to read modem provider from gateway {Gateway}", route.Gateway);
                continue;
            }

            if (!string.IsNullOrWhiteSpace(provider))
            {
                names[route.Device] = provider;
            }
        }

        return names;
    }

    private static async Task<string?> ReadModemProviderAsync(string gateway, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(2));

        using var client = new HttpClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"http://{gateway}/goform/goform_get_cmd_process?isTest=false&cmd=network_type,network_provider,operator,signalbar,wan_ipaddr,modem_main_state");
        request.Headers.Referrer = new Uri($"http://{gateway}/index.html");
        request.Headers.TryAddWithoutValidation("Origin", $"http://{gateway}");
        request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");

        using var response = await client.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            return null;
        }

        var payload = await response.Content.ReadFromJsonAsync<ModemProviderResponse>(
            JsonOptions,
            timeoutCts.Token).ConfigureAwait(false);
        return SelectProviderName(payload);
    }

    public static IReadOnlyList<GatewayRoute> ParseDefaultRoutes(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        try
        {
            var routes = JsonSerializer.Deserialize<List<IpRoute>>(output, JsonOptions) ?? [];
            return routes
                .Where(route => !string.IsNullOrWhiteSpace(route.Device) && !string.IsNullOrWhiteSpace(route.Gateway))
                .Select(route => new GatewayRoute(route.Device!, route.Gateway!))
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string? SelectProviderName(ModemProviderResponse? payload)
    {
        if (payload is null || !string.IsNullOrWhiteSpace(payload.Error))
        {
            return null;
        }

        return FirstUsefulProvider(payload.NetworkProvider) ??
               FirstUsefulProvider(payload.Operator);
    }

    private static string? FirstUsefulProvider(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Equals("Limited Service", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("No Service", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("Searching", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return trimmed;
    }

    private static bool ShouldSkipGatewayProbe(string device, string gateway)
    {
        if (device.StartsWith("xbond", StringComparison.OrdinalIgnoreCase) ||
            device.StartsWith("tailscale", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(device, "lo", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(gateway))
        {
            return true;
        }

        return !IPAddress.TryParse(gateway, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork;
    }

    public static IReadOnlyDictionary<string, string> ParseNmcliDeviceStatus(string output)
    {
        return ParseNmcliDeviceMetadata(output)
            .Where(item => !string.IsNullOrWhiteSpace(item.DisplayName))
            .ToDictionary(item => item.Device, item => item.DisplayName, StringComparer.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<InterfaceMetadata> ParseNmcliDeviceMetadata(string output)
    {
        var interfaces = new List<InterfaceMetadata>();
        foreach (var rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = ParseTerseLine(rawLine);
            if (parts.Count < 4)
            {
                continue;
            }

            var device = parts[0].Trim();
            var type = parts[1].Trim();
            var state = parts[2].Trim();
            var connection = parts[3].Trim();
            if (string.IsNullOrWhiteSpace(device))
            {
                continue;
            }

            interfaces.Add(new InterfaceMetadata(
                device,
                type,
                state,
                connection,
                IsGenericConnectionName(connection, device) ? "" : connection));
        }

        return interfaces;
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

    public sealed record GatewayRoute(string Device, string Gateway);

    public sealed record InterfaceMetadata(
        string Device,
        string Type,
        string State,
        string ConnectionName,
        string DisplayName)
    {
        public bool IsConnected => State.StartsWith("connected", StringComparison.OrdinalIgnoreCase);

        public bool IsDashboardCandidate =>
            IsConnected &&
            (Type.Equals("ethernet", StringComparison.OrdinalIgnoreCase) ||
             Type.Equals("wifi", StringComparison.OrdinalIgnoreCase)) &&
            !Device.StartsWith("xbond", StringComparison.OrdinalIgnoreCase) &&
            !Device.StartsWith("tailscale", StringComparison.OrdinalIgnoreCase) &&
            !Device.StartsWith("p2p-", StringComparison.OrdinalIgnoreCase) &&
            !Device.Equals("lo", StringComparison.OrdinalIgnoreCase);
    }

    public sealed class ModemProviderResponse
    {
        [JsonPropertyName("network_provider")]
        public string? NetworkProvider { get; init; }

        [JsonPropertyName("operator")]
        public string? Operator { get; init; }

        [JsonPropertyName("Error")]
        public string? Error { get; init; }
    }

    private sealed class IpRoute
    {
        [JsonPropertyName("dev")]
        public string? Device { get; init; }

        [JsonPropertyName("gateway")]
        public string? Gateway { get; init; }
    }
}

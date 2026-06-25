using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XNetwork.Services;

public sealed class InterfaceMetadataService
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SlowProviderCacheDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan GatewayProbeTimeout = TimeSpan.FromMilliseconds(750);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly ILogger<InterfaceMetadataService> _logger;
    private readonly HttpClient _httpClient;
    private readonly Func<string, CancellationToken, Task<string?>> _modemProviderFetcher;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly object _providerCacheLock = new();
    private readonly Dictionary<string, CachedProviderName> _providerNameCache = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<InterfaceMetadata> _interfaces = [];
    private DateTimeOffset _expiresAtUtc = DateTimeOffset.MinValue;

    public InterfaceMetadataService(ILogger<InterfaceMetadataService> logger)
        : this(logger, null, null, null)
    {
    }

    public InterfaceMetadataService(
        ILogger<InterfaceMetadataService> logger,
        Func<string, CancellationToken, Task<string?>>? modemProviderFetcher,
        TimeProvider? timeProvider,
        HttpClient? httpClient)
    {
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _httpClient = httpClient ?? new HttpClient { Timeout = GatewayProbeTimeout };
        _modemProviderFetcher = modemProviderFetcher ?? ReadModemProviderAsync;
    }

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

        var now = _timeProvider.GetUtcNow();
        if (now < _expiresAtUtc)
        {
            return _interfaces;
        }

        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = _timeProvider.GetUtcNow();
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

    public async Task<IReadOnlyList<GatewayRoute>> GetDefaultGatewayRoutesAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux())
        {
            return [];
        }

        var routesResult = await RunProcessAsync(
            "ip",
            ["-j", "route", "show", "default"],
            cancellationToken).ConfigureAwait(false);

        return routesResult.ExitCode == 0
            ? ParseDefaultRoutes(routesResult.StandardOutput)
            : [];
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
                _logger.LogDebug("nmcli device status failed: {Error}", result.StandardError.Trim());
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
            _logger.LogDebug(ex, "Unable to refresh interface display names from NetworkManager");
            return [];
        }
    }

    private async Task<IReadOnlyDictionary<string, string>> ReadGatewayProviderNamesAsync(CancellationToken cancellationToken)
    {
        return await ReadGatewayProviderNamesAsync(
            await GetDefaultGatewayRoutesAsync(cancellationToken).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, string>> ReadGatewayProviderNamesAsync(
        IReadOnlyList<GatewayRoute> routes,
        CancellationToken cancellationToken = default)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var tasks = routes
            .Where(route => !ShouldSkipGatewayProbe(route.Device, route.Gateway))
            .Select(route => ReadGatewayProviderNameAsync(route, cancellationToken))
            .ToArray();

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        foreach (var result in results)
        {
            if (result is null || string.IsNullOrWhiteSpace(result.Provider))
            {
                continue;
            }

            names[result.Device] = result.Provider;
        }

        return names;
    }

    private async Task<GatewayProviderResult?> ReadGatewayProviderNameAsync(
        GatewayRoute route,
        CancellationToken cancellationToken)
    {
        var cacheKey = route.Gateway;
        var now = _timeProvider.GetUtcNow();
        lock (_providerCacheLock)
        {
            if (_providerNameCache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAtUtc > now)
            {
                return string.IsNullOrWhiteSpace(cached.Provider)
                    ? null
                    : new GatewayProviderResult(route.Device, cached.Provider);
            }
        }

        string? provider;
        try
        {
            provider = await _modemProviderFetcher(route.Gateway, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            provider = null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to read modem provider from gateway {Gateway}", route.Gateway);
            provider = null;
        }

        lock (_providerCacheLock)
        {
            _providerNameCache[cacheKey] = new CachedProviderName(
                string.IsNullOrWhiteSpace(provider) ? null : provider,
                now + SlowProviderCacheDuration);
        }

        return string.IsNullOrWhiteSpace(provider)
            ? null
            : new GatewayProviderResult(route.Device, provider);
    }

    private async Task<string?> ReadModemProviderAsync(string gateway, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(GatewayProbeTimeout);

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"http://{gateway}/goform/goform_get_cmd_process?isTest=false&cmd=network_type,network_provider,operator,signalbar,wan_ipaddr,modem_main_state");
        request.Headers.Referrer = new Uri($"http://{gateway}/index.html");
        request.Headers.TryAddWithoutValidation("Origin", $"http://{gateway}");
        request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");

        using var response = await _httpClient.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
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

    private static bool IsLocalManagementInterface(string device, string connectionName)
    {
        return string.Equals(device, "eth0", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(connectionName, "netplan-eth0", StringComparison.OrdinalIgnoreCase);
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

    private sealed record CachedProviderName(string? Provider, DateTimeOffset ExpiresAtUtc);

    private sealed record GatewayProviderResult(string Device, string Provider);

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
            !Device.Equals("lo", StringComparison.OrdinalIgnoreCase) &&
            !IsLocalManagementInterface(Device, ConnectionName);
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

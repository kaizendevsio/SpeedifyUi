using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using XNetwork.Models;

namespace XNetwork.Services;

public sealed class F50ModemTelemetryService(
    LocalDeviceProxyService proxyService,
    InterfaceMetadataService interfaceMetadataService,
    ILogger<F50ModemTelemetryService> logger)
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMilliseconds(900);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient _httpClient = new() { Timeout = RequestTimeout };
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private IReadOnlyDictionary<string, F50ModemTelemetry> _byInterface = new Dictionary<string, F50ModemTelemetry>(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _expiresAtUtc = DateTimeOffset.MinValue;

    public async Task<IReadOnlyDictionary<string, F50ModemTelemetry>> GetTelemetryByInterfaceAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        if (now < _expiresAtUtc)
        {
            return _byInterface;
        }

        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (now < _expiresAtUtc)
            {
                return _byInterface;
            }

            _byInterface = await ReadTelemetryByInterfaceAsync(cancellationToken).ConfigureAwait(false);
            _expiresAtUtc = now + CacheDuration;
            return _byInterface;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<IReadOnlyDictionary<string, F50ModemTelemetry>> ReadTelemetryByInterfaceAsync(CancellationToken cancellationToken)
    {
        var entries = proxyService.GetEntries()
            .Where(entry => entry.Enabled && entry.TelemetryEnabled)
            .ToArray();
        if (entries.Length == 0)
        {
            return new Dictionary<string, F50ModemTelemetry>(StringComparer.OrdinalIgnoreCase);
        }

        var routes = await interfaceMetadataService.GetDefaultGatewayRoutesAsync(cancellationToken).ConfigureAwait(false);
        var routeByGateway = routes
            .GroupBy(route => route.Gateway, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Device, StringComparer.OrdinalIgnoreCase);

        var tasks = entries.Select(entry => ReadTelemetryAsync(entry, cancellationToken)).ToArray();
        var telemetry = await Task.WhenAll(tasks).ConfigureAwait(false);
        var result = new Dictionary<string, F50ModemTelemetry>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in telemetry.Where(item => item?.IsAvailable == true).Cast<F50ModemTelemetry>())
        {
            if (routeByGateway.TryGetValue(item.Host, out var device))
            {
                result[device] = item;
            }
        }

        return result;
    }

    private async Task<F50ModemTelemetry?> ReadTelemetryAsync(LocalDeviceProxyEntry entry, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(LocalDeviceProxyService.NormalizeTargetUrl(entry.TargetUrl), UriKind.Absolute, out var baseUri) ||
            string.IsNullOrWhiteSpace(baseUri.Host))
        {
            return null;
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(RequestTimeout);
            var uri = new Uri(baseUri, "/goform/goform_get_cmd_process?isTest=false&cmd=network_type,current_network_type,signalbar&multi_data=1");
            using var response = await _httpClient.GetAsync(uri, timeoutCts.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return null;
            }

            var payload = await response.Content.ReadFromJsonAsync<F50ModemTelemetryResponse>(
                JsonOptions,
                timeoutCts.Token).ConfigureAwait(false);
            if (payload is null || !string.IsNullOrWhiteSpace(payload.Error))
            {
                return null;
            }

            return new F50ModemTelemetry
            {
                Host = baseUri.Host,
                Generation = SelectGeneration(payload),
                SignalBars = SelectSignalBars(payload.SignalBar),
                UpdatedAtUtc = DateTime.UtcNow
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Unable to read F50 telemetry from {Target}", entry.TargetUrl);
            return null;
        }
    }

    public static string? SelectGeneration(F50ModemTelemetryResponse payload)
    {
        var raw = FirstNonEmpty(payload.CurrentNetworkType, payload.NetworkType);
        if (raw is null)
        {
            return null;
        }

        var normalized = raw.Replace("_", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal)
            .Replace(" ", "", StringComparison.Ordinal)
            .ToUpperInvariant();

        if (normalized.Contains("5G", StringComparison.Ordinal) ||
            normalized.Contains("NR", StringComparison.Ordinal) ||
            normalized.Contains("ENDC", StringComparison.Ordinal))
        {
            return "5G";
        }

        if (normalized.Contains("4G", StringComparison.Ordinal) ||
            normalized.Contains("LTE", StringComparison.Ordinal))
        {
            return "4G";
        }

        if (normalized.Contains("3G", StringComparison.Ordinal) ||
            normalized.Contains("WCDMA", StringComparison.Ordinal) ||
            normalized.Contains("UMTS", StringComparison.Ordinal))
        {
            return "3G";
        }

        return null;
    }

    public static int? SelectSignalBars(string? signalBar)
    {
        if (!int.TryParse(signalBar, out var bars))
        {
            return null;
        }

        return Math.Clamp(bars, 0, 5);
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
    }
}


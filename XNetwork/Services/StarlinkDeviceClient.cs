using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.IO.Compression;
using XNetwork.Models;

namespace XNetwork.Services;

public sealed class StarlinkDeviceClient : IDisposable
{
    private static readonly byte[] GetStatusPayload = { 0xE2, 0x3E, 0x00 };
    private readonly StarlinkTelemetrySettings _settings;
    private readonly ILogger<StarlinkDeviceClient> _logger;
    private readonly HttpClient _httpClient;

    public StarlinkDeviceClient(StarlinkTelemetrySettings settings, ILogger<StarlinkDeviceClient> logger)
    {
        _settings = settings;
        _logger = logger;

        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = settings.RequestTimeout,
            EnableMultipleHttp2Connections = true
        };

        _httpClient = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = settings.RequestTimeout
        };
    }

    public async Task<StarlinkTelemetrySnapshot> GetStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await GetStatusOverGrpcAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (_settings.UseGrpcWebFallback && IsTransportFailure(ex))
        {
            _logger.LogDebug(ex, "Starlink h2c gRPC status request failed; trying gRPC-Web fallback");
            return await GetStatusOverGrpcWebAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<StarlinkCapabilitySnapshot> GetCapabilitiesAsync(bool statusAvailable, CancellationToken cancellationToken)
    {
        string? rootHtml = null;
        string? scriptText = null;
        var webUiReachable = false;
        string? error = null;

        try
        {
            var rootUri = new Uri($"http://{_settings.Host}/");
            rootHtml = await _httpClient.GetStringAsync(rootUri, cancellationToken).ConfigureAwait(false);
            webUiReachable = true;

            var scriptPath = FindStarlinkScriptPath(rootHtml);
            if (!string.IsNullOrWhiteSpace(scriptPath))
            {
                var scriptUri = new Uri(rootUri, scriptPath);
                using var response = await _httpClient.GetAsync(scriptUri, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                scriptText = DecodeMaybeGzip(bytes, response.Content.Headers.ContentEncoding.Contains("gzip"));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _logger.LogDebug(ex, "Starlink capability probe failed");
        }

        return StarlinkCapabilityDetector.Detect(
            _settings.Host,
            statusAvailable,
            webUiReachable,
            rootHtml,
            scriptText,
            error);
    }

    private async Task<StarlinkTelemetrySnapshot> GetStatusOverGrpcAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            BuildUri(_settings.GrpcPort))
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = CreateGrpcContent("application/grpc")
        };

        request.Headers.TryAddWithoutValidation("grpc-encoding", "identity");
        request.Headers.TryAddWithoutValidation("grpc-accept-encoding", "identity");

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        return StarlinkStatusParser.ParseGrpcResponse(content);
    }

    private async Task<StarlinkTelemetrySnapshot> GetStatusOverGrpcWebAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            BuildUri(_settings.GrpcWebPort))
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            Content = CreateGrpcContent("application/grpc-web+proto")
        };

        request.Headers.TryAddWithoutValidation("x-grpc-web", "1");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/grpc-web+proto"));

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        return StarlinkStatusParser.ParseGrpcResponse(content);
    }

    private Uri BuildUri(int port)
    {
        return new Uri($"http://{_settings.Host}:{port}/SpaceX.API.Device.Device/Handle");
    }

    private static string? FindStarlinkScriptPath(string rootHtml)
    {
        var match = Regex.Match(
            rootHtml,
            """<script[^>]+src=["'](?<src>[^"']*script\.js(?:\.gz)?[^"']*)["']""",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        return match.Success ? match.Groups["src"].Value : null;
    }

    private static string DecodeMaybeGzip(byte[] bytes, bool hasGzipEncoding)
    {
        if (hasGzipEncoding || bytes is [0x1f, 0x8b, ..])
        {
            using var compressed = new MemoryStream(bytes);
            using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
            using var decompressed = new MemoryStream();
            gzip.CopyTo(decompressed);
            return Encoding.UTF8.GetString(decompressed.ToArray());
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private static ByteArrayContent CreateGrpcContent(string contentType)
    {
        var body = new byte[5 + GetStatusPayload.Length];
        body[0] = 0;
        body[1] = 0;
        body[2] = 0;
        body[3] = 0;
        body[4] = (byte)GetStatusPayload.Length;
        GetStatusPayload.CopyTo(body, 5);

        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return content;
    }

    private static bool IsTransportFailure(Exception ex)
    {
        return ex is HttpRequestException or TaskCanceledException or SocketException or IOException;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}

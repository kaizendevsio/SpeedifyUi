using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using XNetwork.Models;

namespace XNetwork.Services;

public sealed class StarlinkDeviceClient : IDisposable
{
    private static readonly byte[] GetStatusPayload = { 0xE2, 0x3E, 0x00 };
    private readonly StarlinkTelemetrySettings _settings;
    private readonly IStarlinkHttpClientFactory _httpClientFactory;
    private readonly ILogger<StarlinkDeviceClient> _logger;

    public StarlinkDeviceClient(
        StarlinkTelemetrySettings settings,
        IStarlinkHttpClientFactory httpClientFactory,
        ILogger<StarlinkDeviceClient> logger)
    {
        _settings = settings;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
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
            using var lease = await _httpClientFactory.CreateAsync(cancellationToken).ConfigureAwait(false);
            var rootUri = new Uri($"http://{_settings.Host}/");
            rootHtml = await lease.Client.GetStringAsync(rootUri, cancellationToken).ConfigureAwait(false);
            webUiReachable = true;

            var scriptPath = FindStarlinkScriptPath(rootHtml);
            if (!string.IsNullOrWhiteSpace(scriptPath))
            {
                var scriptUri = new Uri(rootUri, scriptPath);
                using var response = await lease.Client.GetAsync(scriptUri, cancellationToken).ConfigureAwait(false);
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

    public async Task<StarlinkCommandResult> ExecuteCommandAsync(string command, CancellationToken cancellationToken)
    {
        var normalizedCommand = NormalizeCommand(command);
        var payload = StarlinkCommandPayloads.CreatePayload(normalizedCommand);

        try
        {
            using var lease = await _httpClientFactory.CreateAsync(cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                BuildUri(_settings.GrpcWebPort))
            {
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
                Content = CreateGrpcContent("application/grpc-web+proto", payload)
            };

            request.Headers.TryAddWithoutValidation("x-grpc-web", "1");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/grpc-web+proto"));

            using var response = await lease.Client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            StarlinkCommandResponseParser.EnsureSuccess(content);

            return new StarlinkCommandResult
            {
                Command = normalizedCommand,
                Success = true,
                Message = $"{FormatCommand(normalizedCommand)} command sent to Starlink"
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Starlink command {Command} failed", normalizedCommand);

            return new StarlinkCommandResult
            {
                Command = normalizedCommand,
                Success = false,
                Message = $"{FormatCommand(normalizedCommand)} command failed: {ex.Message}"
            };
        }
    }

    private async Task<StarlinkTelemetrySnapshot> GetStatusOverGrpcAsync(CancellationToken cancellationToken)
    {
        using var lease = await _httpClientFactory.CreateAsync(cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            BuildUri(_settings.GrpcPort))
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = CreateGrpcContent("application/grpc", GetStatusPayload)
        };

        request.Headers.TryAddWithoutValidation("grpc-encoding", "identity");
        request.Headers.TryAddWithoutValidation("grpc-accept-encoding", "identity");

        using var response = await lease.Client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        return StarlinkStatusParser.ParseGrpcResponse(content);
    }

    private async Task<StarlinkTelemetrySnapshot> GetStatusOverGrpcWebAsync(CancellationToken cancellationToken)
    {
        using var lease = await _httpClientFactory.CreateAsync(cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            BuildUri(_settings.GrpcWebPort))
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            Content = CreateGrpcContent("application/grpc-web+proto", GetStatusPayload)
        };

        request.Headers.TryAddWithoutValidation("x-grpc-web", "1");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/grpc-web+proto"));

        using var response = await lease.Client.SendAsync(request, cancellationToken).ConfigureAwait(false);
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

    private static ByteArrayContent CreateGrpcContent(string contentType, byte[] payload)
    {
        var body = new byte[5 + payload.Length];
        body[0] = 0;
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(1, 4), (uint)payload.Length);
        payload.CopyTo(body, 5);

        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return content;
    }

    private static string NormalizeCommand(string command)
    {
        return command.Trim().ToLowerInvariant();
    }

    private static string FormatCommand(string command)
    {
        return command switch
        {
            "reboot" => "Reboot",
            "stow" => "Stow",
            "unstow" => "Unstow",
            "dish_clear_obstruction_map" => "Reset obstruction map",
            _ => command
        };
    }

    private static bool IsTransportFailure(Exception ex)
    {
        return ex is HttpRequestException or TaskCanceledException or SocketException or IOException;
    }

    public void Dispose()
    {
    }
}

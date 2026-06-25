using System.Net;

namespace XNetwork.Services;

public sealed class LocalDevicePortProxyHostedService(
    LocalDeviceProxyService proxyService,
    ILogger<LocalDevicePortProxyHostedService> logger) : BackgroundService
{
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection",
        "Keep-Alive",
        "Proxy-Authenticate",
        "Proxy-Authorization",
        "TE",
        "Trailer",
        "Transfer-Encoding",
        "Upgrade",
        "Host",
        "Origin",
        "Referer"
    };

    private readonly Dictionary<int, HttpListener> _listeners = new();
    private readonly object _lock = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            SyncListeners(stoppingToken);
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            foreach (var listener in _listeners.Values)
            {
                StopListener(listener);
            }

            _listeners.Clear();
        }

        return base.StopAsync(cancellationToken);
    }

    public static Uri? BuildPortTargetUri(string targetUrl, string? rawUrl)
    {
        if (!Uri.TryCreate(LocalDeviceProxyService.NormalizeTargetUrl(targetUrl), UriKind.Absolute, out var baseUri))
        {
            return null;
        }

        rawUrl = string.IsNullOrWhiteSpace(rawUrl) ? "/" : rawUrl;
        if (!rawUrl.StartsWith('/'))
        {
            rawUrl = "/" + rawUrl;
        }

        var query = "";
        var path = rawUrl;
        var queryIndex = rawUrl.IndexOf('?', StringComparison.Ordinal);
        if (queryIndex >= 0)
        {
            path = rawUrl[..queryIndex];
            query = rawUrl[(queryIndex + 1)..];
        }

        var basePath = baseUri.AbsolutePath.TrimEnd('/');
        var requestPath = path.TrimStart('/');
        var combinedPath = string.IsNullOrWhiteSpace(requestPath)
            ? (string.IsNullOrWhiteSpace(basePath) ? "/" : basePath + "/")
            : $"{basePath}/{requestPath}";

        var builder = new UriBuilder(baseUri)
        {
            Path = combinedPath,
            Query = query
        };
        return builder.Uri;
    }

    private void SyncListeners(CancellationToken stoppingToken)
    {
        var desiredPorts = proxyService.GetEnabledPortEntries()
            .Where(entry => entry.ListenPort is not null)
            .Select(entry => entry.ListenPort!.Value)
            .Distinct()
            .ToHashSet();

        lock (_lock)
        {
            foreach (var port in _listeners.Keys.Except(desiredPorts).ToArray())
            {
                StopListener(_listeners[port]);
                _listeners.Remove(port);
                logger.LogInformation("Stopped local device port proxy on {Port}", port);
            }

            foreach (var port in desiredPorts.Except(_listeners.Keys).ToArray())
            {
                try
                {
                    var listener = new HttpListener
                    {
                        IgnoreWriteExceptions = true
                    };
                    listener.Prefixes.Add($"http://*:{port}/");
                    listener.Start();
                    _listeners[port] = listener;
                    _ = Task.Run(() => AcceptLoopAsync(port, listener, stoppingToken), stoppingToken);
                    logger.LogInformation("Started local device port proxy on {Port}", port);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not start local device port proxy on {Port}", port);
                }
            }
        }
    }

    private async Task AcceptLoopAsync(int port, HttpListener listener, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().WaitAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Port proxy accept failed on {Port}", port);
                continue;
            }

            _ = Task.Run(() => HandleRequestAsync(port, context, stoppingToken), stoppingToken);
        }
    }

    private async Task HandleRequestAsync(int port, HttpListenerContext context, CancellationToken cancellationToken)
    {
        var proxy = proxyService.GetEnabledPortEntry(port);
        if (proxy is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await WriteTextAsync(context.Response, "Proxy port is not configured.", cancellationToken).ConfigureAwait(false);
            return;
        }

        var targetUri = BuildPortTargetUri(proxy.TargetUrl, context.Request.RawUrl);
        if (targetUri is null)
        {
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            await WriteTextAsync(context.Response, "Proxy target is invalid.", cancellationToken).ConfigureAwait(false);
            return;
        }

        using var request = CreateProxyRequest(context.Request, targetUri);
        try
        {
            using var response = await HttpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            context.Response.StatusCode = (int)response.StatusCode;
            CopyResponseHeaders(response, context.Response);
            await response.Content.CopyToAsync(context.Response.OutputStream, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Local device port proxy {Port} to {Target} failed", port, proxy.TargetUrl);
            if (context.Response.OutputStream.CanWrite)
            {
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                await WriteTextAsync(context.Response, "Local device proxy target did not respond.", cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            SafeClose(context.Response);
        }
    }

    private static HttpRequestMessage CreateProxyRequest(HttpListenerRequest source, Uri targetUri)
    {
        var request = new HttpRequestMessage(new HttpMethod(source.HttpMethod), targetUri);
        if (source.HasEntityBody)
        {
            request.Content = new StreamContent(source.InputStream);
        }

        foreach (var key in source.Headers.AllKeys)
        {
            if (string.IsNullOrWhiteSpace(key) || HopByHopHeaders.Contains(key))
            {
                continue;
            }

            var values = source.Headers.GetValues(key);
            if (values is null)
            {
                continue;
            }

            if (!request.Headers.TryAddWithoutValidation(key, values) && request.Content is not null)
            {
                request.Content.Headers.TryAddWithoutValidation(key, values);
            }
        }

        request.Headers.Host = targetUri.IsDefaultPort
            ? targetUri.Host
            : $"{targetUri.Host}:{targetUri.Port}";

        RewriteSecurityContextHeaders(source, request, targetUri);
        return request;
    }

    public static Uri BuildTargetOrigin(Uri targetUri)
    {
        var builder = new UriBuilder(targetUri.Scheme, targetUri.Host, targetUri.IsDefaultPort ? -1 : targetUri.Port)
        {
            Path = "",
            Query = "",
            Fragment = ""
        };
        return builder.Uri;
    }

    public static Uri BuildTargetReferer(Uri targetUri, string? sourceReferer)
    {
        var origin = BuildTargetOrigin(targetUri);
        var builder = new UriBuilder(origin)
        {
            Path = "/"
        };

        if (!Uri.TryCreate(sourceReferer, UriKind.Absolute, out var referer))
        {
            return builder.Uri;
        }

        builder.Path = string.IsNullOrWhiteSpace(referer.AbsolutePath) ? "/" : referer.AbsolutePath;
        builder.Query = referer.Query.TrimStart('?');
        builder.Fragment = referer.Fragment.TrimStart('#');
        return builder.Uri;
    }

    private static void RewriteSecurityContextHeaders(
        HttpListenerRequest source,
        HttpRequestMessage request,
        Uri targetUri)
    {
        if (!string.IsNullOrWhiteSpace(source.Headers["Origin"]))
        {
            request.Headers.TryAddWithoutValidation("Origin", BuildTargetOrigin(targetUri).GetLeftPart(UriPartial.Authority));
        }

        if (!string.IsNullOrWhiteSpace(source.Headers["Referer"]))
        {
            request.Headers.Referrer = BuildTargetReferer(targetUri, source.Headers["Referer"]);
        }
    }

    private static void CopyResponseHeaders(HttpResponseMessage response, HttpListenerResponse target)
    {
        foreach (var header in response.Headers)
        {
            AddResponseHeader(target, header.Key, header.Value);
        }

        foreach (var header in response.Content.Headers)
        {
            AddResponseHeader(target, header.Key, header.Value);
        }
    }

    private static void AddResponseHeader(HttpListenerResponse response, string key, IEnumerable<string> values)
    {
        if (HopByHopHeaders.Contains(key))
        {
            return;
        }

        try
        {
            if (string.Equals(key, "Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                response.ContentType = values.FirstOrDefault();
                return;
            }

            if (string.Equals(key, "Content-Length", StringComparison.OrdinalIgnoreCase) &&
                long.TryParse(values.FirstOrDefault(), out var contentLength))
            {
                response.ContentLength64 = contentLength;
                return;
            }

            foreach (var value in values)
            {
                response.Headers.Add(key, value);
            }
        }
        catch
        {
            // Some headers are restricted by HttpListener. The body is still forwarded unchanged.
        }
    }

    private static async Task WriteTextAsync(
        HttpListenerResponse response,
        string message,
        CancellationToken cancellationToken)
    {
        response.ContentType = "text/plain; charset=utf-8";
        await using var writer = new StreamWriter(response.OutputStream, leaveOpen: true);
        await writer.WriteAsync(message.AsMemory(), cancellationToken).ConfigureAwait(false);
        SafeClose(response);
    }

    private static void StopListener(HttpListener listener)
    {
        try
        {
            listener.Stop();
        }
        catch
        {
        }

        try
        {
            listener.Close();
        }
        catch
        {
        }
    }

    private static void SafeClose(HttpListenerResponse response)
    {
        try
        {
            response.Close();
        }
        catch
        {
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(5)
        };
        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
    }
}

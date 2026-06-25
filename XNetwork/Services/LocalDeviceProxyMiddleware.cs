using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace XNetwork.Services;

public sealed class LocalDeviceProxyMiddleware(
    RequestDelegate next,
    LocalDeviceProxyService proxyService,
    ILogger<LocalDeviceProxyMiddleware> logger)
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
        "Host"
    };

    public async Task InvokeAsync(HttpContext context)
    {
        var requestPath = context.Request.Path.Value ?? "/";
        var proxy = proxyService.GetEnabledEntries()
            .Where(entry => LocalDeviceProxyService.PathMatches(requestPath, entry.ExposedRoute))
            .OrderByDescending(entry => entry.ExposedRoute.Length)
            .FirstOrDefault();

        if (proxy is null)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var targetUri = BuildTargetUri(proxy.TargetUrl, proxy.ExposedRoute, requestPath, context.Request.QueryString.Value);
        if (targetUri is null)
        {
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            await context.Response.WriteAsync("Proxy target is invalid.").ConfigureAwait(false);
            return;
        }

        using var request = CreateProxyRequest(context, targetUri);
        try
        {
            using var response = await HttpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
            context.RequestAborted).ConfigureAwait(false);

            context.Response.StatusCode = (int)response.StatusCode;
            var rewriteBody = ShouldRewriteBody(response.Content.Headers.ContentType);
            CopyResponseHeaders(response, context, proxy, rewriteBody);
            if (rewriteBody)
            {
                var body = await response.Content.ReadAsStringAsync(context.RequestAborted).ConfigureAwait(false);
                await context.Response.WriteAsync(
                    RewriteLocalDeviceBody(body, proxy.ExposedRoute),
                    context.RequestAborted).ConfigureAwait(false);
            }
            else
            {
                await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Local device proxy {Route} to {Target} failed", proxy.ExposedRoute, proxy.TargetUrl);
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                await context.Response.WriteAsync("Local device proxy target did not respond.").ConfigureAwait(false);
            }
        }
    }

    public static Uri? BuildTargetUri(string targetUrl, string exposedRoute, string requestPath, string? queryString)
    {
        if (!Uri.TryCreate(LocalDeviceProxyService.NormalizeTargetUrl(targetUrl), UriKind.Absolute, out var baseUri))
        {
            return null;
        }

        exposedRoute = LocalDeviceProxyService.NormalizeRoute(exposedRoute);
        requestPath = LocalDeviceProxyService.NormalizeRoute(requestPath);
        var remainingPath = requestPath.Length <= exposedRoute.Length
            ? ""
            : requestPath[exposedRoute.Length..];
        if (string.IsNullOrEmpty(remainingPath))
        {
            remainingPath = "/";
        }

        var basePath = baseUri.AbsolutePath.TrimEnd('/');
        var combinedPath = basePath + remainingPath;
        var builder = new UriBuilder(baseUri)
        {
            Path = combinedPath,
            Query = string.IsNullOrWhiteSpace(queryString) ? "" : queryString.TrimStart('?')
        };
        return builder.Uri;
    }

    public static string RewriteLocalDeviceBody(string body, string exposedRoute)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return body;
        }

        var route = LocalDeviceProxyService.NormalizeRoute(exposedRoute);
        body = Regex.Replace(
            body,
            @"(?<prefix>\b(?:href|src|action)\s*=\s*[""'])/(?!/)",
            match => $"{match.Groups["prefix"].Value}{route}/",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        body = Regex.Replace(
            body,
            @"(?<prefix>\b(?:fetch|open)\(\s*[""'])/(?!/)",
            match => $"{match.Groups["prefix"].Value}{route}/",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        body = Regex.Replace(
            body,
            @"(?<prefix>url\(\s*[""']?)/(?!/)",
            match => $"{match.Groups["prefix"].Value}{route}/",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return body;
    }

    private static HttpRequestMessage CreateProxyRequest(HttpContext context, Uri targetUri)
    {
        var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), targetUri);
        if (!HttpMethods.IsGet(context.Request.Method) &&
            !HttpMethods.IsHead(context.Request.Method) &&
            !HttpMethods.IsDelete(context.Request.Method) &&
            context.Request.ContentLength is > 0)
        {
            request.Content = new StreamContent(context.Request.Body);
        }

        foreach (var header in context.Request.Headers)
        {
            if (HopByHopHeaders.Contains(header.Key))
            {
                continue;
            }

            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()) && request.Content is not null)
            {
                request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
        }

        request.Headers.Host = targetUri.IsDefaultPort
            ? targetUri.Host
            : $"{targetUri.Host}:{targetUri.Port}";
        return request;
    }

    private static void CopyResponseHeaders(
        HttpResponseMessage response,
        HttpContext context,
        Models.LocalDeviceProxyEntry proxy,
        bool rewriteBody)
    {
        foreach (var header in response.Headers)
        {
            if (HopByHopHeaders.Contains(header.Key))
            {
                continue;
            }

            if (string.Equals(header.Key, "Location", StringComparison.OrdinalIgnoreCase))
            {
                var location = header.Value.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(location) && location.StartsWith('/'))
                {
                    context.Response.Headers[header.Key] = LocalDeviceProxyService.NormalizeRoute(proxy.ExposedRoute) + location;
                    continue;
                }
            }
            else if (string.Equals(header.Key, "Set-Cookie", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.Headers[header.Key] = header.Value
                    .Select(value => Regex.Replace(
                        value,
                        @"(?<=;\s*Path=)/(?!/)",
                        LocalDeviceProxyService.NormalizeRoute(proxy.ExposedRoute) + "/",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    .ToArray();
                continue;
            }

            context.Response.Headers[header.Key] = header.Value.ToArray();
        }

        foreach (var header in response.Content.Headers)
        {
            if (HopByHopHeaders.Contains(header.Key))
            {
                continue;
            }

            if (rewriteBody && string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            context.Response.Headers[header.Key] = header.Value.ToArray();
        }

        context.Response.Headers.Remove("transfer-encoding");
        if (rewriteBody)
        {
            context.Response.Headers.Remove("content-length");
        }
    }

    private static bool ShouldRewriteBody(MediaTypeHeaderValue? contentType)
    {
        var mediaType = contentType?.MediaType;
        return mediaType is not null &&
               (mediaType.Contains("html", StringComparison.OrdinalIgnoreCase) ||
                mediaType.Contains("javascript", StringComparison.OrdinalIgnoreCase) ||
                mediaType.Contains("css", StringComparison.OrdinalIgnoreCase));
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

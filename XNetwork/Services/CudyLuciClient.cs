using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using XNetwork.Models;

namespace XNetwork.Services;

public class CudyLuciClient(ILogger<CudyLuciClient> logger)
{
    private const string LoginPath = "cgi-bin/luci";
    private const string ComboPath = "cgi-bin/luci/admin/network/wireless/config/combo";
    private const string CombinePath = "cgi-bin/luci/admin/network/wireless/config/combine";
    private const string UncombinePath = "cgi-bin/luci/admin/network/wireless/config/uncombine";
    private const string XRouterDeviceListPath = "cgi-bin/luci/admin/network/devices/devlist?detail=1";
    private const string XRouterDeviceInfoPath = "cgi-bin/luci/admin/network/devices/devinfo";
    private const string XRouterInternetPath = "cgi-bin/luci/admin/network/devices/internet";

    public async Task SetWirelessEnabledAsync(CudyApAutomationSettings settings, bool enabled, CancellationToken cancellationToken = default)
    {
        if (!settings.Disable2G && !settings.Disable5G)
        {
            throw new InvalidOperationException("At least one Cudy Wi-Fi band must be selected.");
        }

        using var session = await CreateAuthenticatedSessionAsync(settings, cancellationToken).ConfigureAwait(false);
        var baseUri = session.BaseUri;
        var client = session.Client;

        var smartConnect = await IsSmartConnectEnabledAsync(client, baseUri, cancellationToken).ConfigureAwait(false);
        var formPath = smartConnect ? CombinePath : UncombinePath;
        var formHtml = await GetStringAsync(client, BuildUri(baseUri, formPath), cancellationToken).ConfigureAwait(false);
        var fields = CudyLuciFormParser.ParseFields(formHtml);

        fields["timeclock"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        fields["cbi.submit"] = "1";
        fields["cbi.apply"] = "Save & Apply";

        if (smartConnect)
        {
            fields["cbid.wireless.wlan.disabled"] = enabled ? "0" : "1";
        }
        else
        {
            if (settings.Disable2G)
            {
                fields["cbid.wireless.wlan00.disabled"] = enabled ? "0" : "1";
            }

            if (settings.Disable5G)
            {
                fields["cbid.wireless.wlan10.disabled"] = enabled ? "0" : "1";
            }
        }

        var action = CudyLuciFormParser.ParseFormAction(formHtml) ?? formPath;
        logger.LogInformation("Setting Cudy AP enabled={Enabled} using {Mode} wireless form at {BaseUrl}", enabled, smartConnect ? "combined" : "split-band", baseUri);
        await PostMultipartAsync(client, ResolveUri(baseUri, action), fields, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<XRouterClient>> GetXRouterClientsAsync(CudyApAutomationSettings settings, CancellationToken cancellationToken = default)
    {
        using var session = await CreateAuthenticatedSessionAsync(settings, cancellationToken).ConfigureAwait(false);
        var html = await GetStringAsync(session.Client, BuildUri(session.BaseUri, XRouterDeviceListPath), cancellationToken).ConfigureAwait(false);
        return CudyXRouterClientParser.ParseClients(html);
    }

    public async Task SetXRouterClientInternetAccessAsync(CudyApAutomationSettings settings, XRouterClient target, bool allowed, CancellationToken cancellationToken = default)
    {
        if (target.InternetAllowed == allowed)
        {
            return;
        }

        using var session = await CreateAuthenticatedSessionAsync(settings, cancellationToken).ConfigureAwait(false);
        var query = BuildXRouterClientQuery(target, target.InternetAllowed);
        var uri = BuildUri(session.BaseUri, XRouterInternetPath + "?" + query);
        logger.LogInformation("Setting Wifi client {MacAddress} internet allowed={Allowed}", target.MacAddress, allowed);
        var response = await session.Client.PostFormAsync(uri, new Dictionary<string, string>(), cancellationToken).ConfigureAwait(false);
        var responseText = response.Content;
        response.EnsureSuccessStatusCode();

        if (ContainsLoginPrompt(responseText))
        {
            throw new InvalidOperationException("Cudy returned the login page while changing Wifi client internet access.");
        }
    }

    public async Task SetXRouterClientRateLimitAsync(CudyApAutomationSettings settings, XRouterClient target, bool enabled, int? downloadMbps, int? uploadMbps, CancellationToken cancellationToken = default)
    {
        using var session = await CreateAuthenticatedSessionAsync(settings, cancellationToken).ConfigureAwait(false);
        var query = BuildXRouterClientQuery(target, target.InternetAllowed);
        var path = XRouterDeviceInfoPath + "?" + query;
        var formHtml = await GetStringAsync(session.Client, BuildUri(session.BaseUri, path), cancellationToken).ConfigureAwait(false);
        var fields = CudyLuciFormParser.ParseFields(formHtml);

        fields["timeclock"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        fields["cbi.submit"] = "1";
        fields["cbi.apply"] = "Save & Apply";
        fields["cbid.luci._devname.hostname"] = target.Hostname;
        fields["cbi.cbe.luci._devname.limit"] = "1";
        fields["cbid.luci._devname.limit"] = enabled ? "1" : "0";
        fields["cbid.luci._devname.ddrate"] = enabled ? Math.Max(1, downloadMbps ?? 1).ToString() : "";
        fields["cbid.luci._devname.uurate"] = enabled ? Math.Max(1, uploadMbps ?? 1).ToString() : "";

        var action = CudyLuciFormParser.ParseFormAction(formHtml) ?? path;
        logger.LogInformation("Setting Wifi client {MacAddress} rate limit enabled={Enabled}", target.MacAddress, enabled);
        await PostMultipartAsync(session.Client, ResolveUri(session.BaseUri, action), fields, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AuthenticatedCudySession> CreateAuthenticatedSessionAsync(CudyApAutomationSettings settings, CancellationToken cancellationToken)
    {
        var baseUri = BuildBaseUri(settings.ManagementBaseUrl);
        var password = ResolveAdminPassword(settings);
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException("Cudy admin password is not configured. Set CudyApAutomation:AdminPassword or the configured environment variable.");
        }

        var client = new LenientCudyHttpSession(baseUri, TimeSpan.FromSeconds(Math.Clamp(settings.RequestTimeoutSeconds, 3, 60)));

        try
        {
            await LoginAsync(client, baseUri, password, cancellationToken).ConfigureAwait(false);
            return new AuthenticatedCudySession(baseUri, client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task LoginAsync(LenientCudyHttpSession client, Uri baseUri, string password, CancellationToken cancellationToken)
    {
        var loginUri = BuildUri(baseUri, LoginPath);
        var loginHtml = await GetStringAsync(client, loginUri, cancellationToken).ConfigureAwait(false);
        var fields = CudyLuciFormParser.ParseFields(loginHtml);
        fields.TryGetValue("token", out var token);
        fields.TryGetValue("salt", out var salt);
        fields.TryGetValue("_csrf", out var csrf);

        var hashedPassword = string.IsNullOrEmpty(salt) ? password : Sha256Hex(password + salt);
        if (!string.IsNullOrEmpty(token) && !string.IsNullOrEmpty(salt))
        {
            hashedPassword = Sha256Hex(hashedPassword + token);
        }

        var postFields = new Dictionary<string, string>
        {
            ["zonename"] = "UTC",
            ["timeclock"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
            ["luci_language"] = "auto",
            ["luci_username"] = "admin",
            ["luci_password"] = hashedPassword
        };

        if (!string.IsNullOrEmpty(csrf))
        {
            postFields["_csrf"] = csrf;
        }

        if (!string.IsNullOrEmpty(token))
        {
            postFields["token"] = token;
        }

        if (!string.IsNullOrEmpty(salt))
        {
            postFields["salt"] = salt;
        }

        var loginAction = CudyLuciFormParser.ParseFormAction(loginHtml);
        var postUri = string.IsNullOrWhiteSpace(loginAction) ? loginUri : ResolveUri(baseUri, loginAction);

        var response = await client.PostFormAsync(postUri, postFields, cancellationToken).ConfigureAwait(false);
        var responseHtml = response.Content;

        if (ContainsLoginPrompt(responseHtml))
        {
            throw new InvalidOperationException("Cudy login failed. Check the configured admin password.");
        }

        response.EnsureSuccessStatusCode();
    }

    private static async Task<bool> IsSmartConnectEnabledAsync(LenientCudyHttpSession client, Uri baseUri, CancellationToken cancellationToken)
    {
        var comboHtml = await GetStringAsync(client, BuildUri(baseUri, ComboPath), cancellationToken).ConfigureAwait(false);
        var fields = CudyLuciFormParser.ParseFields(comboHtml);
        return fields.TryGetValue("cbid.wireless.smart.connect", out var value) && value == "1";
    }

    private static async Task<string> GetStringAsync(LenientCudyHttpSession client, Uri uri, CancellationToken cancellationToken)
    {
        var response = await client.GetAsync(uri, cancellationToken).ConfigureAwait(false);
        var content = response.Content;

        if (response.StatusCode == HttpStatusCode.Forbidden && IsLoginPath(uri) && ContainsLoginPrompt(content))
        {
            return content;
        }

        response.EnsureSuccessStatusCode();

        if (ContainsLoginPrompt(content) && !IsLoginPath(uri))
        {
            throw new InvalidOperationException("Cudy returned the login page while reading wireless settings.");
        }

        return content;
    }

    private async Task PostMultipartAsync(LenientCudyHttpSession client, Uri uri, Dictionary<string, string> fields, CancellationToken cancellationToken)
    {
        var boundary = "----xnetworkcudy" + Guid.NewGuid().ToString("N");
        var builder = new StringBuilder();
        foreach (var field in fields)
        {
            builder.Append("--").Append(boundary).Append("\r\n");
            builder.Append("Content-Disposition: form-data; name=\"").Append(EscapeMultipartName(field.Key)).Append("\"\r\n\r\n");
            builder.Append(field.Value).Append("\r\n");
        }

        builder.Append("--").Append(boundary).Append("--\r\n");
        var response = await client.PostAsync(
            uri,
            "multipart/form-data; boundary=" + boundary,
            Encoding.UTF8.GetBytes(builder.ToString()),
            cancellationToken).ConfigureAwait(false);
        var responseHtml = response.Content;
        response.EnsureSuccessStatusCode();

        if (ContainsLoginPrompt(responseHtml))
        {
            throw new InvalidOperationException("Cudy returned the login page while applying wireless settings.");
        }

        await ApplyPendingServicesAsync(client, uri, responseHtml, cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplyPendingServicesAsync(LenientCudyHttpSession client, Uri formUri, string responseHtml, CancellationToken cancellationToken)
    {
        var match = Regex.Match(responseHtml, @"\$\.post\('(?<path>[^']+)'\s*,\s*\{\s*token:\s*'(?<token>[^']+)'", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!match.Success)
        {
            return;
        }

        var applyUri = ResolveUri(formUri, match.Groups["path"].Value);
        var statusUri = ResolveUri(formUri, "/cgi-bin/luci/admin/servicectl/status");
        logger.LogInformation("Applying Cudy service changes using {ApplyUri}", applyUri);
        var applyResponse = await client.PostFormAsync(applyUri, new Dictionary<string, string>
        {
            ["token"] = match.Groups["token"].Value
        }, cancellationToken).ConfigureAwait(false);
        applyResponse.EnsureSuccessStatusCode();

        for (var attempt = 0; attempt < 45; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            var statusResponse = await client.GetAsync(statusUri, cancellationToken).ConfigureAwait(false);
            var status = statusResponse.Content.Trim();
            statusResponse.EnsureSuccessStatusCode();
            if (status.Equals("finish", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation("Cudy service changes applied");
                return;
            }
        }

        throw new TimeoutException("Timed out waiting for Cudy service changes to finish applying.");
    }

    private static string EscapeMultipartName(string value)
    {
        return value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    private static Uri BuildBaseUri(string managementBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(managementBaseUrl))
        {
            throw new InvalidOperationException("Cudy management URL is not configured.");
        }

        var value = managementBaseUrl.Trim();
        if (!value.Contains("://", StringComparison.Ordinal))
        {
            value = "http://" + value;
        }

        if (value.EndsWith("/cgi-bin/luci", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^"/cgi-bin/luci".Length];
        }
        else if (value.EndsWith("/cgi-bin/luci/", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^"/cgi-bin/luci/".Length];
        }

        if (!value.EndsWith('/'))
        {
            value += "/";
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("Cudy management URL is invalid.");
        }

        return uri;
    }

    private static Uri BuildUri(Uri baseUri, string relativePath)
    {
        return new Uri(baseUri, relativePath);
    }

    private static Uri ResolveUri(Uri baseUri, string action)
    {
        var value = action.Trim();
        if (value.StartsWith('/'))
        {
            return new Uri(new Uri(baseUri.GetLeftPart(UriPartial.Authority)), value);
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var absoluteUri))
        {
            if (absoluteUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                absoluteUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return absoluteUri;
            }

            throw new InvalidOperationException($"Cudy form action uses unsupported URL scheme '{absoluteUri.Scheme}'.");
        }

        return new Uri(baseUri, value);
    }

    private static string ResolveAdminPassword(CudyApAutomationSettings settings)
    {
        if (!string.IsNullOrEmpty(settings.AdminPassword))
        {
            return settings.AdminPassword;
        }

        return string.IsNullOrWhiteSpace(settings.AdminPasswordEnvironmentVariable)
            ? ""
            : Environment.GetEnvironmentVariable(settings.AdminPasswordEnvironmentVariable.Trim()) ?? "";
    }

    private static string BuildXRouterClientQuery(XRouterClient client, bool internetAllowed)
    {
        var fields = new Dictionary<string, string>
        {
            ["macaddr"] = client.MacAddress,
            ["hostname"] = client.Hostname,
            ["internet"] = internetAllowed ? "1" : "0",
            ["vpn"] = client.VpnEnabled ? "1" : "0",
            ["dnsfilter"] = client.DnsFilterEnabled ? "1" : "0"
        };

        return string.Join("&", fields.Select(field =>
            Uri.EscapeDataString(field.Key) + "=" + Uri.EscapeDataString(field.Value)));
    }

    private static bool ValidateServerCertificate(Uri uri, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors sslErrors)
    {
        return sslErrors == SslPolicyErrors.None || IsPrivateHttpsHost(uri);
    }

    private static bool IsPrivateHttpsHost(Uri? uri)
    {
        if (uri?.Scheme != Uri.UriSchemeHttps || !IPAddress.TryParse(uri.Host, out var address))
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        var bytes = address.GetAddressBytes();
        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => bytes[0] == 10 ||
                                           bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31 ||
                                           bytes[0] == 192 && bytes[1] == 168 ||
                                           bytes[0] == 169 && bytes[1] == 254,
            AddressFamily.InterNetworkV6 => address.IsIPv6LinkLocal || (bytes[0] & 0xfe) == 0xfc,
            _ => false
        };
    }

    private static bool ContainsLoginPrompt(string html)
    {
        return html.Contains("cbi-modal-auth", StringComparison.OrdinalIgnoreCase) ||
               html.Contains("luci_password", StringComparison.OrdinalIgnoreCase) &&
               html.Contains("luci_username", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLoginPath(Uri uri)
    {
        return uri.AbsolutePath.TrimEnd('/').EndsWith("/cgi-bin/luci", StringComparison.OrdinalIgnoreCase);
    }

    private static string Sha256Hex(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private sealed class LenientCudyHttpSession(Uri baseUri, TimeSpan timeout) : IDisposable
    {
        private const int MaxRedirects = 5;
        private readonly Dictionary<string, string> _cookies = new(StringComparer.OrdinalIgnoreCase);

        public Task<LenientCudyHttpResponse> GetAsync(Uri uri, CancellationToken cancellationToken)
        {
            return SendAsync("GET", uri, null, null, cancellationToken);
        }

        public Task<LenientCudyHttpResponse> PostFormAsync(Uri uri, IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken)
        {
            var body = Encoding.UTF8.GetBytes(FormEncode(fields));
            return PostAsync(uri, "application/x-www-form-urlencoded", body, cancellationToken);
        }

        public Task<LenientCudyHttpResponse> PostAsync(Uri uri, string contentType, byte[] body, CancellationToken cancellationToken)
        {
            return SendAsync("POST", uri, contentType, body, cancellationToken);
        }

        public void Dispose()
        {
        }

        private async Task<LenientCudyHttpResponse> SendAsync(
            string method,
            Uri uri,
            string? contentType,
            byte[]? body,
            CancellationToken cancellationToken,
            int redirectsRemaining = MaxRedirects)
        {
            if (!uri.IsAbsoluteUri)
            {
                uri = new Uri(baseUri, uri);
            }

            var response = await SendOnceAsync(method, uri, contentType, body, cancellationToken).ConfigureAwait(false);
            if (redirectsRemaining <= 0 || !IsRedirect(response.StatusCode) || !response.TryGetHeader("Location", out var location))
            {
                return response;
            }

            var nextUri = ResolveUri(uri, location);
            return await SendAsync("GET", nextUri, null, null, cancellationToken, redirectsRemaining - 1).ConfigureAwait(false);
        }

        private async Task<LenientCudyHttpResponse> SendOnceAsync(
            string method,
            Uri uri,
            string? contentType,
            byte[]? body,
            CancellationToken cancellationToken)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            var token = timeoutCts.Token;

            using var tcpClient = new TcpClient();
            await tcpClient.ConnectAsync(uri.Host, uri.Port, token).ConfigureAwait(false);
            Stream stream = tcpClient.GetStream();

            if (uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                var sslStream = new SslStream(stream, leaveInnerStreamOpen: false, (sender, certificate, chain, errors) =>
                    ValidateServerCertificate(uri, certificate, chain, errors));
                await sslStream.AuthenticateAsClientAsync(uri.Host).WaitAsync(token).ConfigureAwait(false);
                stream = sslStream;
            }
            else if (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Cudy URL uses unsupported scheme '{uri.Scheme}'.");
            }

            var header = BuildRequestHeader(method, uri, contentType, body?.Length ?? 0);
            var headerBytes = Encoding.ASCII.GetBytes(header);
            await stream.WriteAsync(headerBytes, token).ConfigureAwait(false);
            if (body is { Length: > 0 })
            {
                await stream.WriteAsync(body, token).ConfigureAwait(false);
            }

            await stream.FlushAsync(token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, token).ConfigureAwait(false);
            var response = LenientCudyHttpResponse.Parse(buffer.ToArray());
            StoreCookies(response);
            return response;
        }

        private string BuildRequestHeader(string method, Uri uri, string? contentType, int contentLength)
        {
            var builder = new StringBuilder();
            builder.Append(method).Append(' ').Append(string.IsNullOrWhiteSpace(uri.PathAndQuery) ? "/" : uri.PathAndQuery).Append(" HTTP/1.1\r\n");
            builder.Append("Host: ").Append(GetHostHeader(uri)).Append("\r\n");
            builder.Append("User-Agent: XNetwork-Cudy/1.0\r\n");
            builder.Append("Accept: text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8\r\n");
            builder.Append("Connection: close\r\n");

            if (_cookies.Count > 0)
            {
                builder.Append("Cookie: ").Append(string.Join("; ", _cookies.Select(cookie => cookie.Key + "=" + cookie.Value))).Append("\r\n");
            }

            if (contentType is not null)
            {
                builder.Append("Content-Type: ").Append(contentType).Append("\r\n");
                builder.Append("Content-Length: ").Append(contentLength.ToString()).Append("\r\n");
            }

            builder.Append("\r\n");
            return builder.ToString();
        }

        private void StoreCookies(LenientCudyHttpResponse response)
        {
            foreach (var value in response.GetHeaders("Set-Cookie"))
            {
                var cookiePair = value.Split(';', 2)[0];
                var equalsIndex = cookiePair.IndexOf('=');
                if (equalsIndex <= 0)
                {
                    continue;
                }

                var name = cookiePair[..equalsIndex].Trim();
                var cookieValue = cookiePair[(equalsIndex + 1)..].Trim();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    _cookies[name] = cookieValue;
                }
            }
        }

        private static string GetHostHeader(Uri uri)
        {
            if (uri.IsDefaultPort)
            {
                return uri.Host;
            }

            return uri.Host.Contains(':', StringComparison.Ordinal) ? $"[{uri.Host}]:{uri.Port}" : $"{uri.Host}:{uri.Port}";
        }

        private static string FormEncode(IReadOnlyDictionary<string, string> fields)
        {
            return string.Join("&", fields.Select(field =>
                FormEscape(field.Key) + "=" + FormEscape(field.Value)));
        }

        private static string FormEscape(string value)
        {
            return Uri.EscapeDataString(value).Replace("%20", "+", StringComparison.Ordinal);
        }

        private static bool IsRedirect(HttpStatusCode statusCode)
        {
            var code = (int)statusCode;
            return code is >= 300 and <= 399;
        }
    }

    private sealed class LenientCudyHttpResponse
    {
        private LenientCudyHttpResponse(HttpStatusCode statusCode, string content, Dictionary<string, List<string>> headers)
        {
            StatusCode = statusCode;
            Content = content;
            _headers = headers;
        }

        private readonly Dictionary<string, List<string>> _headers;

        public HttpStatusCode StatusCode { get; }

        public string Content { get; }

        public static LenientCudyHttpResponse Parse(byte[] responseBytes)
        {
            var (headerBytes, bodyBytes) = SplitHeadersAndBody(responseBytes);
            var headerText = Encoding.ASCII.GetString(headerBytes);
            var lines = headerText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            var statusCode = ParseStatusCode(lines.FirstOrDefault());
            var headers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var line in lines.Skip(1))
            {
                var colonIndex = line.IndexOf(':');
                if (colonIndex <= 0)
                {
                    continue;
                }

                var name = line[..colonIndex].Trim();
                if (!IsValidHeaderName(name))
                {
                    continue;
                }

                var value = line[(colonIndex + 1)..].Trim();
                if (!headers.TryGetValue(name, out var values))
                {
                    values = [];
                    headers[name] = values;
                }

                values.Add(value);
            }

            if (headers.TryGetValue("Transfer-Encoding", out var transferEncodings) &&
                transferEncodings.Any(value => value.Contains("chunked", StringComparison.OrdinalIgnoreCase)))
            {
                bodyBytes = DecodeChunkedBody(bodyBytes);
            }

            var content = Encoding.UTF8.GetString(bodyBytes);
            return new LenientCudyHttpResponse(statusCode, content, headers);
        }

        public void EnsureSuccessStatusCode()
        {
            var code = (int)StatusCode;
            if (code is < 200 or > 299)
            {
                throw new HttpRequestException($"Cudy returned HTTP {(int)StatusCode}.", null, StatusCode);
            }
        }

        public bool TryGetHeader(string name, out string value)
        {
            if (_headers.TryGetValue(name, out var values) && values.Count > 0)
            {
                value = values[0];
                return true;
            }

            value = "";
            return false;
        }

        public IReadOnlyList<string> GetHeaders(string name)
        {
            return _headers.TryGetValue(name, out var values) ? values : [];
        }

        private static (byte[] HeaderBytes, byte[] BodyBytes) SplitHeadersAndBody(byte[] responseBytes)
        {
            var separatorIndex = IndexOf(responseBytes, "\r\n\r\n"u8.ToArray());
            var separatorLength = 4;
            if (separatorIndex < 0)
            {
                separatorIndex = IndexOf(responseBytes, "\n\n"u8.ToArray());
                separatorLength = 2;
            }

            if (separatorIndex < 0)
            {
                return (responseBytes, []);
            }

            return (responseBytes[..separatorIndex], responseBytes[(separatorIndex + separatorLength)..]);
        }

        private static int IndexOf(byte[] source, byte[] pattern)
        {
            for (var i = 0; i <= source.Length - pattern.Length; i++)
            {
                var matched = true;
                for (var j = 0; j < pattern.Length; j++)
                {
                    if (source[i + j] == pattern[j])
                    {
                        continue;
                    }

                    matched = false;
                    break;
                }

                if (matched)
                {
                    return i;
                }
            }

            return -1;
        }

        private static HttpStatusCode ParseStatusCode(string? statusLine)
        {
            if (string.IsNullOrWhiteSpace(statusLine))
            {
                throw new HttpRequestException("Cudy returned an empty HTTP response.");
            }

            var parts = statusLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !int.TryParse(parts[1], out var statusCode))
            {
                throw new HttpRequestException("Cudy returned an invalid HTTP status line.");
            }

            return (HttpStatusCode)statusCode;
        }

        private static bool IsValidHeaderName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            foreach (var character in name)
            {
                if (character is <= ' ' or >= (char)127 ||
                    "()<>@,;:\\\"/[]?={}".Contains(character, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private static byte[] DecodeChunkedBody(byte[] bodyBytes)
        {
            try
            {
                using var decoded = new MemoryStream();
                var offset = 0;
                while (offset < bodyBytes.Length)
                {
                    var lineEnd = IndexOf(bodyBytes[offset..], "\r\n"u8.ToArray());
                    if (lineEnd < 0)
                    {
                        return bodyBytes;
                    }

                    var sizeLine = Encoding.ASCII.GetString(bodyBytes, offset, lineEnd);
                    var semicolonIndex = sizeLine.IndexOf(';');
                    if (semicolonIndex >= 0)
                    {
                        sizeLine = sizeLine[..semicolonIndex];
                    }

                    if (!int.TryParse(sizeLine.Trim(), System.Globalization.NumberStyles.HexNumber, null, out var size))
                    {
                        return bodyBytes;
                    }

                    offset += lineEnd + 2;
                    if (size == 0)
                    {
                        break;
                    }

                    if (offset + size > bodyBytes.Length)
                    {
                        return bodyBytes;
                    }

                    decoded.Write(bodyBytes, offset, size);
                    offset += size;
                    if (offset + 2 <= bodyBytes.Length && bodyBytes[offset] == '\r' && bodyBytes[offset + 1] == '\n')
                    {
                        offset += 2;
                    }
                }

                return decoded.ToArray();
            }
            catch
            {
                return bodyBytes;
            }
        }
    }

    private sealed class AuthenticatedCudySession(Uri baseUri, LenientCudyHttpSession client) : IDisposable
    {
        public Uri BaseUri { get; } = baseUri;

        public LenientCudyHttpSession Client { get; } = client;

        public void Dispose()
        {
            Client.Dispose();
        }
    }
}

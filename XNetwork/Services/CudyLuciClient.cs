using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using XNetwork.Models;

namespace XNetwork.Services;

public class CudyLuciClient(ILogger<CudyLuciClient> logger)
{
    private const string LoginPath = "cgi-bin/luci";
    private const string ComboPath = "cgi-bin/luci/admin/network/wireless/config/combo";
    private const string CombinePath = "cgi-bin/luci/admin/network/wireless/config/combine";
    private const string UncombinePath = "cgi-bin/luci/admin/network/wireless/config/uncombine";

    public async Task SetWirelessEnabledAsync(CudyApAutomationSettings settings, bool enabled, CancellationToken cancellationToken = default)
    {
        if (!settings.Disable2G && !settings.Disable5G)
        {
            throw new InvalidOperationException("At least one Cudy Wi-Fi band must be selected.");
        }

        var baseUri = BuildBaseUri(settings.ManagementBaseUrl);
        var password = ResolveAdminPassword(settings);
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException("Cudy admin password is not configured. Set CudyApAutomation:AdminPassword or the configured environment variable.");
        }

        using var handler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            AllowAutoRedirect = true,
            ServerCertificateCustomValidationCallback = ValidateServerCertificate
        };
        using var client = new HttpClient(handler)
        {
            BaseAddress = baseUri,
            Timeout = TimeSpan.FromSeconds(Math.Clamp(settings.RequestTimeoutSeconds, 3, 60))
        };

        await LoginAsync(client, baseUri, password, cancellationToken).ConfigureAwait(false);

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

    private static async Task LoginAsync(HttpClient client, Uri baseUri, string password, CancellationToken cancellationToken)
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

        using var content = new FormUrlEncodedContent(postFields);
        using var response = await client.PostAsync(postUri, content, cancellationToken).ConfigureAwait(false);
        var responseHtml = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (ContainsLoginPrompt(responseHtml))
        {
            throw new InvalidOperationException("Cudy login failed. Check the configured admin password.");
        }

        response.EnsureSuccessStatusCode();
    }

    private static async Task<bool> IsSmartConnectEnabledAsync(HttpClient client, Uri baseUri, CancellationToken cancellationToken)
    {
        var comboHtml = await GetStringAsync(client, BuildUri(baseUri, ComboPath), cancellationToken).ConfigureAwait(false);
        var fields = CudyLuciFormParser.ParseFields(comboHtml);
        return fields.TryGetValue("cbid.wireless.smart.connect", out var value) && value == "1";
    }

    private static async Task<string> GetStringAsync(HttpClient client, Uri uri, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(uri, cancellationToken).ConfigureAwait(false);
        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

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

    private static async Task PostMultipartAsync(HttpClient client, Uri uri, Dictionary<string, string> fields, CancellationToken cancellationToken)
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
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(builder.ToString()));
        content.Headers.ContentType = new MediaTypeHeaderValue("multipart/form-data");
        content.Headers.ContentType.Parameters.Add(new NameValueHeaderValue("boundary", boundary));

        using var response = await client.PostAsync(uri, content, cancellationToken).ConfigureAwait(false);
        var responseHtml = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        if (ContainsLoginPrompt(responseHtml))
        {
            throw new InvalidOperationException("Cudy returned the login page while applying wireless settings.");
        }
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

    private static bool ValidateServerCertificate(HttpRequestMessage request, X509Certificate2? certificate, X509Chain? chain, SslPolicyErrors sslErrors)
    {
        return sslErrors == SslPolicyErrors.None || IsPrivateHttpsHost(request.RequestUri);
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
}

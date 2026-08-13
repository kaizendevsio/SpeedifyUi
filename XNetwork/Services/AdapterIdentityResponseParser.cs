using System.Globalization;
using System.Text.Json;
using XNetwork.Models;

namespace XNetwork.Services;

/// <summary>
/// Parses keyless ISP lookup responses. Handles both the ipwho.is shape (nested "connection"
/// object, "success" flag) and the ip-api.com shape (flat fields, "status" string).
/// </summary>
public static class AdapterIdentityResponseParser
{
    private static readonly string[] CorporateSuffixes =
    [
        "inc.", "inc", "incorporated", "ltd.", "ltd", "limited", "llc", "l.l.c.",
        "corp.", "corp", "corporation", "co.", "co", "company", "s.a.", "sa", "plc", "pte", "pvt"
    ];

    public static bool TryParse(string? json, out AdapterIdentity? identity)
    {
        identity = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (root.TryGetProperty("success", out var success) &&
                success.ValueKind == JsonValueKind.False)
            {
                return false;
            }

            if (ReadString(root, "status") is { } status &&
                !status.Equals("success", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var connection = root.TryGetProperty("connection", out var connectionElement) &&
                             connectionElement.ValueKind == JsonValueKind.Object
                ? connectionElement
                : default;

            var publicIp = ReadString(root, "ip") ?? ReadString(root, "query");
            var isp = ReadString(connection, "isp") ?? ReadString(root, "isp");
            var organization = ReadString(connection, "org") ?? ReadString(root, "org");
            var asLabel = ReadAsLabel(connection, root);

            if (string.IsNullOrWhiteSpace(publicIp) &&
                string.IsNullOrWhiteSpace(isp) &&
                string.IsNullOrWhiteSpace(organization))
            {
                return false;
            }

            identity = new AdapterIdentity
            {
                PublicIp = publicIp,
                Isp = isp,
                Organization = organization,
                AsLabel = asLabel,
                City = ReadString(root, "city"),
                Region = ReadString(root, "region") ?? ReadString(root, "regionName"),
                Country = ReadString(root, "country"),
                CountryCode = ReadString(root, "country_code") ?? ReadString(root, "countryCode"),
                DisplayName = NormalizeProviderName(isp) ?? NormalizeProviderName(organization)
            };

            return true;
        }
    }

    /// <summary>
    /// Turns a raw provider string into a short display name: drops a leading AS number token and
    /// trailing corporate suffixes, and collapses whitespace. Never returns an empty string.
    /// </summary>
    public static string? NormalizeProviderName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var tokens = value
            .Replace(',', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (tokens.Count == 0)
        {
            return null;
        }

        if (tokens.Count > 1 && IsAsToken(tokens[0]))
        {
            tokens.RemoveAt(0);
        }

        while (tokens.Count > 1 && CorporateSuffixes.Contains(tokens[^1], StringComparer.OrdinalIgnoreCase))
        {
            tokens.RemoveAt(tokens.Count - 1);
        }

        var normalized = string.Join(' ', tokens).Trim();
        return string.IsNullOrWhiteSpace(normalized) ? value.Trim() : normalized;
    }

    private static bool IsAsToken(string token)
    {
        return token.Length > 2 &&
               token.StartsWith("AS", StringComparison.OrdinalIgnoreCase) &&
               token[2..].All(char.IsDigit);
    }

    private static string? ReadAsLabel(JsonElement connection, JsonElement root)
    {
        if (connection.ValueKind == JsonValueKind.Object &&
            connection.TryGetProperty("asn", out var asn))
        {
            if (asn.ValueKind == JsonValueKind.Number && asn.TryGetInt64(out var asnNumber))
            {
                return $"AS{asnNumber.ToString(CultureInfo.InvariantCulture)}";
            }

            if (asn.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(asn.GetString()))
            {
                var raw = asn.GetString()!.Trim();
                return raw.StartsWith("AS", StringComparison.OrdinalIgnoreCase) ? raw : $"AS{raw}";
            }
        }

        return ReadString(root, "as");
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = property.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}

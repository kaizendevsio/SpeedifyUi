using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using XNetwork.Models;

namespace XNetwork.Services;

public static class CudyXRouterClientParser
{
    private static readonly Regex RowRegex = new("<tr\\b(?=[^>]*\\bdata-sid=)[^>]*>(?<body>.*?)</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex TagRegex = new("<.*?>", RegexOptions.Singleline);

    public static IReadOnlyList<XRouterClient> ParseClients(string html)
    {
        var clients = new List<XRouterClient>();
        foreach (Match rowMatch in RowRegex.Matches(html))
        {
            var row = rowMatch.Groups["body"].Value;
            var hostLines = SplitLines(ExtractFieldText(row, "hostname"));
            var ipMacLines = SplitLines(ExtractFieldText(row, "ipmac"));
            var macAddress = ipMacLines.FirstOrDefault(IsMacAddress) ?? ExtractQueryValue(row, "macaddr");
            if (string.IsNullOrWhiteSpace(macAddress))
            {
                continue;
            }

            var speedHtml = ExtractFieldHtml(row, "speed");
            var (uploadMbps, downloadMbps) = ParseThroughput(speedHtml);

            clients.Add(new XRouterClient
            {
                Hostname = hostLines.FirstOrDefault() ?? macAddress,
                ConnectionType = hostLines.Skip(1).FirstOrDefault() ?? "Unknown",
                IpAddress = ipMacLines.FirstOrDefault(IsIpAddress) ?? "",
                MacAddress = NormalizeMacAddress(macAddress),
                UploadMbps = uploadMbps,
                DownloadMbps = downloadMbps,
                SignalDbm = ParseSignalDbm(ExtractFieldText(row, "signal")),
                OnlineDuration = ExtractFieldText(row, "online"),
                InternetAllowed = ParseInternetAllowed(ExtractFieldHtml(row, "internet")),
                VpnEnabled = ExtractQueryValue(row, "vpn") == "1",
                DnsFilterEnabled = ExtractQueryValue(row, "dnsfilter") != "0"
            });
        }

        return clients
            .GroupBy(client => client.MacAddress, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(client => client.DownloadMbps + client.UploadMbps)
            .ThenBy(client => client.Hostname, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string ExtractFieldHtml(string rowHtml, string field)
    {
        var pattern = "<div\\s+id=[\"']cbi-table-\\d+-" + Regex.Escape(field) + "[\"'][^>]*>(?<body>.*?)</div>";
        var match = Regex.Match(rowHtml, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? match.Groups["body"].Value : "";
    }

    private static string ExtractFieldText(string rowHtml, string field)
    {
        var fieldHtml = ExtractFieldHtml(rowHtml, field);
        var preferred = Regex.Match(fieldHtml, "<p\\b[^>]*class=[\"'][^\"']*form-control-static[^\"']*hidden-xs[^\"']*[\"'][^>]*>(?<text>.*?)</p>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return CleanText(preferred.Success ? preferred.Groups["text"].Value : fieldHtml);
    }

    private static string CleanText(string value)
    {
        var withLines = Regex.Replace(value, "<br\\s*/?>", "\n", RegexOptions.IgnoreCase);
        var text = WebUtility.HtmlDecode(TagRegex.Replace(withLines, " ")).Replace('\u00a0', ' ');
        text = Regex.Replace(text, "[ \\t\\f\\v]+", " ");
        text = Regex.Replace(text, " ?\\n ?", "\n");
        return text.Trim();
    }

    private static string[] SplitLines(string value)
    {
        return value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static (double uploadMbps, double downloadMbps) ParseThroughput(string fieldHtml)
    {
        var text = CleanText(fieldHtml).Replace('\n', ' ');
        var matches = Regex.Matches(text, "(?<value>\\d+(?:\\.\\d+)?)\\s*(?<unit>[KMGT]?bps)", RegexOptions.IgnoreCase);
        var upload = matches.Count > 0 ? ToMbps(matches[0]) : 0;
        var download = matches.Count > 1 ? ToMbps(matches[1]) : 0;
        return (upload, download);
    }

    private static double ToMbps(Match match)
    {
        var value = double.Parse(match.Groups["value"].Value, CultureInfo.InvariantCulture);
        return match.Groups["unit"].Value.ToLowerInvariant() switch
        {
            "bps" => value / 1_000_000,
            "kbps" => value / 1_000,
            "mbps" => value,
            "gbps" => value * 1_000,
            "tbps" => value * 1_000_000,
            _ => value
        };
    }

    private static int? ParseSignalDbm(string value)
    {
        var match = Regex.Match(value, "-?\\d+");
        return match.Success && int.TryParse(match.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var signal)
            ? signal
            : null;
    }

    private static bool ParseInternetAllowed(string internetHtml)
    {
        var hidden = Regex.Match(internetHtml, "name=[\"']cbid\\.table\\.\\d+\\.internet[\"'][^>]*value=[\"'](?<value>[01])[\"']", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (hidden.Success)
        {
            return hidden.Groups["value"].Value == "1";
        }

        return !internetHtml.Contains("fa-toggle-off", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractQueryValue(string html, string name)
    {
        var pattern = "(?:[?&]|&amp;)" + Regex.Escape(name) + "=(?<value>[^&\"'\\s<>]+)";
        var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
        return match.Success ? WebUtility.HtmlDecode(Uri.UnescapeDataString(match.Groups["value"].Value)) : "";
    }

    private static bool IsIpAddress(string value)
    {
        return System.Net.IPAddress.TryParse(value, out _);
    }

    private static bool IsMacAddress(string value)
    {
        return Regex.IsMatch(value, "^[0-9A-Fa-f]{2}(?::[0-9A-Fa-f]{2}){5}$");
    }

    private static string NormalizeMacAddress(string value)
    {
        return string.Join(':', value.Split(':').Select(part => part.ToUpperInvariant()));
    }
}

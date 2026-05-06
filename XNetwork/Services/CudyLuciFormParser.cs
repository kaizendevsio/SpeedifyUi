using System.Net;
using System.Text.RegularExpressions;

namespace XNetwork.Services;

public static class CudyLuciFormParser
{
    private static readonly Regex InputRegex = new("<input\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex SelectRegex = new("<select\\b(?<attrs>[^>]*)>(?<body>.*?)</select>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex OptionRegex = new("<option\\b(?<attrs>[^>]*)>(?<text>.*?)</option>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex FormRegex = new("<form\\b(?<attrs>[^>]*)>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex AttributeRegex = new("(?<name>[^\\s=/>]+)(?:\\s*=\\s*(?:\"(?<dq>[^\"]*)\"|'(?<sq>[^']*)'|(?<bare>[^\\s\"'=<>`]+)))?", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex TagRegex = new("<.*?>", RegexOptions.Singleline);

    public static Dictionary<string, string> ParseFields(string html)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (Match match in InputRegex.Matches(html))
        {
            var attributes = ParseAttributes(match.Value);
            if (!TryGetUsableFieldName(attributes, out var name))
            {
                continue;
            }

            var type = GetAttribute(attributes, "type");
            if (IsSkippedInputType(type))
            {
                continue;
            }

            fields[name] = GetAttribute(attributes, "value") ?? "";
        }

        foreach (Match match in SelectRegex.Matches(html))
        {
            var attributes = ParseAttributes(match.Groups["attrs"].Value);
            if (!TryGetUsableFieldName(attributes, out var name))
            {
                continue;
            }

            var option = FindSelectedOption(match.Groups["body"].Value);
            if (option != null)
            {
                fields[name] = option;
            }
        }

        return fields;
    }

    public static string? ParseFormAction(string html)
    {
        foreach (Match match in FormRegex.Matches(html))
        {
            var attributes = ParseAttributes(match.Groups["attrs"].Value);
            var action = GetAttribute(attributes, "action");
            if (!string.IsNullOrWhiteSpace(action) && action != "#")
            {
                return action;
            }
        }

        return null;
    }

    private static bool TryGetUsableFieldName(Dictionary<string, string?> attributes, out string name)
    {
        name = GetAttribute(attributes, "name") ?? "";
        return !string.IsNullOrWhiteSpace(name) && !attributes.ContainsKey("disabled");
    }

    private static bool IsSkippedInputType(string? type)
    {
        return type is not null &&
            (type.Equals("submit", StringComparison.OrdinalIgnoreCase) ||
             type.Equals("button", StringComparison.OrdinalIgnoreCase) ||
             type.Equals("file", StringComparison.OrdinalIgnoreCase) ||
             type.Equals("image", StringComparison.OrdinalIgnoreCase));
    }

    private static string? FindSelectedOption(string body)
    {
        string? firstOption = null;

        foreach (Match match in OptionRegex.Matches(body))
        {
            var attributes = ParseAttributes(match.Groups["attrs"].Value);
            var value = GetAttribute(attributes, "value") ?? StripTags(match.Groups["text"].Value).Trim();
            firstOption ??= value;

            if (attributes.ContainsKey("selected"))
            {
                return value;
            }
        }

        return firstOption;
    }

    private static Dictionary<string, string?> ParseAttributes(string html)
    {
        var attributes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in AttributeRegex.Matches(html))
        {
            var name = match.Groups["name"].Value;
            if (string.IsNullOrWhiteSpace(name) || name.StartsWith('<'))
            {
                continue;
            }

            var value = match.Groups["dq"].Success
                ? match.Groups["dq"].Value
                : match.Groups["sq"].Success
                    ? match.Groups["sq"].Value
                    : match.Groups["bare"].Success
                        ? match.Groups["bare"].Value
                        : null;
            attributes[name] = value is null ? null : WebUtility.HtmlDecode(value);
        }

        return attributes;
    }

    private static string? GetAttribute(Dictionary<string, string?> attributes, string name)
    {
        return attributes.TryGetValue(name, out var value) ? value : null;
    }

    private static string StripTags(string value)
    {
        return WebUtility.HtmlDecode(TagRegex.Replace(value, ""));
    }
}

using XNetwork.Models;

namespace XNetwork.Services;

public sealed class LocalDeviceProxyService(
    LocalDeviceProxySettings settings,
    LocalDeviceProxySettingsStore store)
{
    public const int DefaultMinimumListenPort = 18080;
    public const int DefaultMaximumListenPort = 18999;

    private static readonly string[] ReservedRoutes =
    [
        "/",
        "/settings",
        "/wifi",
        "/xrouter",
        "/xbond",
        "/details",
        "/api",
        "/_blazor",
        "/_framework",
        "/_content",
        "/css",
        "/js",
        "/icons"
    ];

    private readonly object _lock = new();

    public IReadOnlyList<LocalDeviceProxyEntry> GetEntries()
    {
        lock (_lock)
        {
            return settings.Entries.Select(Clone).ToArray();
        }
    }

    public IReadOnlyList<LocalDeviceProxyEntry> GetEnabledEntries()
    {
        lock (_lock)
        {
            return settings.Entries
                .Where(entry => entry.Enabled)
                .Select(Clone)
                .ToArray();
        }
    }

    public IReadOnlyList<LocalDeviceProxyEntry> GetEnabledRouteEntries()
    {
        lock (_lock)
        {
            return settings.Entries
                .Where(entry => entry.Enabled && IsRouteMode(entry))
                .Select(Clone)
                .ToArray();
        }
    }

    public IReadOnlyList<LocalDeviceProxyEntry> GetEnabledPortEntries()
    {
        lock (_lock)
        {
            return settings.Entries
                .Where(entry => entry.Enabled && IsPortMode(entry) && entry.ListenPort is not null)
                .Select(Clone)
                .ToArray();
        }
    }

    public LocalDeviceProxyEntry? GetEnabledPortEntry(int port)
    {
        lock (_lock)
        {
            return settings.Entries
                .Where(entry => entry.Enabled && IsPortMode(entry) && entry.ListenPort == port)
                .Select(Clone)
                .FirstOrDefault();
        }
    }

    public async Task SaveEntryAsync(LocalDeviceProxyEntry entry, CancellationToken cancellationToken = default)
    {
        entry = Clone(entry);
        entry.Id = string.IsNullOrWhiteSpace(entry.Id) ? Guid.NewGuid().ToString("N") : entry.Id.Trim();
        entry.ProxyMode = LocalDeviceProxyModes.Normalize(entry.ProxyMode);
        entry.ExposedRoute = NormalizeRoute(entry.ExposedRoute);
        entry.TargetUrl = NormalizeTargetUrl(entry.TargetUrl);
        entry.DisplayName = entry.DisplayName.Trim();

        var validation = ValidateEntry(entry, entry.Id);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(string.Join(" ", validation.Errors));
        }

        lock (_lock)
        {
            var existingIndex = settings.Entries.FindIndex(item => string.Equals(item.Id, entry.Id, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
            {
                settings.Entries[existingIndex] = Clone(entry);
            }
            else
            {
                settings.Entries.Add(Clone(entry));
            }
        }

        await store.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteEntryAsync(string id, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            settings.Entries.RemoveAll(entry => string.Equals(entry.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        await store.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
    }

    public LocalDeviceProxyValidationResult ValidateEntry(LocalDeviceProxyEntry entry, string? currentId = null)
    {
        var result = new LocalDeviceProxyValidationResult();
        var proxyMode = LocalDeviceProxyModes.Normalize(entry.ProxyMode);
        var route = NormalizeRoute(entry.ExposedRoute);
        var target = NormalizeTargetUrl(entry.TargetUrl);

        if (string.IsNullOrWhiteSpace(entry.DisplayName))
        {
            result.Errors.Add("Name is required.");
        }

        if (proxyMode == LocalDeviceProxyModes.Port)
        {
            if (entry.ListenPort is null)
            {
                result.Errors.Add("Local port is required for port proxy mode.");
            }
            else if (entry.ListenPort < DefaultMinimumListenPort || entry.ListenPort > DefaultMaximumListenPort)
            {
                result.Errors.Add($"Local port must be between {DefaultMinimumListenPort} and {DefaultMaximumListenPort}.");
            }
        }
        else if (string.IsNullOrWhiteSpace(route) || !route.StartsWith('/'))
        {
            result.Errors.Add("Exposed route must start with /.");
        }

        if (!string.IsNullOrWhiteSpace(route) && route.Length > 1 && route.EndsWith('/'))
        {
            result.Errors.Add("Exposed route must not end with /.");
        }
        else if (!string.IsNullOrWhiteSpace(route) &&
                 ReservedRoutes.Any(reserved => RouteCollides(route, reserved)))
        {
            result.Errors.Add("Exposed route collides with an app route.");
        }

        if (!Uri.TryCreate(target, UriKind.Absolute, out var targetUri) ||
            (targetUri.Scheme != Uri.UriSchemeHttp && targetUri.Scheme != Uri.UriSchemeHttps))
        {
            result.Errors.Add("Target URL must be http:// or https://.");
        }

        lock (_lock)
        {
            if (proxyMode == LocalDeviceProxyModes.Port &&
                entry.ListenPort is not null &&
                settings.Entries.Any(existing =>
                    !string.Equals(existing.Id, currentId, StringComparison.OrdinalIgnoreCase) &&
                    IsPortMode(existing) &&
                    existing.ListenPort == entry.ListenPort))
            {
                result.Errors.Add("Local port is already used by another proxy.");
            }

            if (proxyMode == LocalDeviceProxyModes.Route &&
                settings.Entries.Any(existing =>
                    !string.Equals(existing.Id, currentId, StringComparison.OrdinalIgnoreCase) &&
                    IsRouteMode(existing) &&
                    string.Equals(NormalizeRoute(existing.ExposedRoute), route, StringComparison.OrdinalIgnoreCase)))
            {
                result.Errors.Add("Exposed route is already used by another proxy.");
            }
        }

        return result;
    }

    public static string NormalizeRoute(string? route)
    {
        route = (route ?? "").Trim();
        if (string.IsNullOrWhiteSpace(route))
        {
            return "";
        }

        if (!route.StartsWith('/'))
        {
            route = "/" + route;
        }

        return route.Length > 1 ? route.TrimEnd('/') : route;
    }

    public static string NormalizeTargetUrl(string? targetUrl)
    {
        targetUrl = (targetUrl ?? "").Trim();
        return targetUrl.Length > 1 ? targetUrl.TrimEnd('/') : targetUrl;
    }

    public static bool PathMatches(string requestPath, string exposedRoute)
    {
        requestPath = NormalizeRoute(requestPath);
        exposedRoute = NormalizeRoute(exposedRoute);
        if (string.IsNullOrWhiteSpace(exposedRoute))
        {
            return false;
        }

        return string.Equals(requestPath, exposedRoute, StringComparison.OrdinalIgnoreCase) ||
               requestPath.StartsWith(exposedRoute + "/", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsPortMode(LocalDeviceProxyEntry entry)
    {
        return string.Equals(LocalDeviceProxyModes.Normalize(entry.ProxyMode), LocalDeviceProxyModes.Port, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsRouteMode(LocalDeviceProxyEntry entry)
    {
        return !IsPortMode(entry);
    }

    public string? BuildLocalProxyUrl(LocalDeviceProxyEntry entry, Uri currentUri)
    {
        if (!IsPortMode(entry) || entry.ListenPort is not { } port)
        {
            return null;
        }

        var builder = new UriBuilder(currentUri.Scheme, currentUri.Host, port)
        {
            Path = "/",
            Query = ""
        };
        return builder.Uri.ToString();
    }

    public LocalDeviceProxyEntry? FindAdminProxyForAdapter(
        string? interfaceName,
        string? displayName,
        string? gateway)
    {
        var entries = GetEnabledPortEntries();
        if (!string.IsNullOrWhiteSpace(gateway))
        {
            var gatewayMatch = entries.FirstOrDefault(entry =>
                string.Equals(GetTargetHost(entry), gateway, StringComparison.OrdinalIgnoreCase));
            if (gatewayMatch is not null)
            {
                return gatewayMatch;
            }
        }

        var normalizedName = NormalizeName(displayName);
        var normalizedInterface = NormalizeName(interfaceName);
        return entries.FirstOrDefault(entry =>
        {
            var entryName = NormalizeName(entry.DisplayName);
            if (string.IsNullOrWhiteSpace(entryName))
            {
                return false;
            }

            return (!string.IsNullOrWhiteSpace(normalizedName) &&
                    (normalizedName.Contains(entryName, StringComparison.OrdinalIgnoreCase) ||
                     entryName.Contains(normalizedName, StringComparison.OrdinalIgnoreCase))) ||
                   (!string.IsNullOrWhiteSpace(normalizedInterface) &&
                    (normalizedInterface.Contains(entryName, StringComparison.OrdinalIgnoreCase) ||
                     entryName.Contains(normalizedInterface, StringComparison.OrdinalIgnoreCase)));
        });
    }

    public static string? GetTargetHost(LocalDeviceProxyEntry entry)
    {
        return Uri.TryCreate(NormalizeTargetUrl(entry.TargetUrl), UriKind.Absolute, out var uri)
            ? uri.Host
            : null;
    }

    private static bool RouteCollides(string route, string reserved)
    {
        route = NormalizeRoute(route);
        reserved = NormalizeRoute(reserved);
        return string.Equals(route, reserved, StringComparison.OrdinalIgnoreCase) ||
               route.StartsWith(reserved + "/", StringComparison.OrdinalIgnoreCase) ||
               reserved.StartsWith(route + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static LocalDeviceProxyEntry Clone(LocalDeviceProxyEntry entry)
    {
        return new LocalDeviceProxyEntry
        {
            Id = entry.Id,
            DisplayName = entry.DisplayName ?? "",
            ProxyMode = LocalDeviceProxyModes.Normalize(entry.ProxyMode),
            ListenPort = entry.ListenPort,
            ExposedRoute = NormalizeRoute(entry.ExposedRoute),
            TargetUrl = NormalizeTargetUrl(entry.TargetUrl),
            Enabled = entry.Enabled,
            TelemetryEnabled = entry.TelemetryEnabled
        };
    }

    private static string NormalizeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        return new string(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
    }
}

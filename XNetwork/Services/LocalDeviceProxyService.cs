using XNetwork.Models;

namespace XNetwork.Services;

public sealed class LocalDeviceProxyService(
    LocalDeviceProxySettings settings,
    LocalDeviceProxySettingsStore store)
{
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

    public async Task SaveEntryAsync(LocalDeviceProxyEntry entry, CancellationToken cancellationToken = default)
    {
        entry = Clone(entry);
        entry.Id = string.IsNullOrWhiteSpace(entry.Id) ? Guid.NewGuid().ToString("N") : entry.Id.Trim();
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
        var route = NormalizeRoute(entry.ExposedRoute);
        var target = NormalizeTargetUrl(entry.TargetUrl);

        if (string.IsNullOrWhiteSpace(entry.DisplayName))
        {
            result.Errors.Add("Name is required.");
        }

        if (string.IsNullOrWhiteSpace(route) || !route.StartsWith('/'))
        {
            result.Errors.Add("Exposed route must start with /.");
        }
        else if (route.Length > 1 && route.EndsWith('/'))
        {
            result.Errors.Add("Exposed route must not end with /.");
        }
        else if (ReservedRoutes.Any(reserved => RouteCollides(route, reserved)))
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
            if (settings.Entries.Any(existing =>
                    !string.Equals(existing.Id, currentId, StringComparison.OrdinalIgnoreCase) &&
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
        return string.Equals(requestPath, exposedRoute, StringComparison.OrdinalIgnoreCase) ||
               requestPath.StartsWith(exposedRoute + "/", StringComparison.OrdinalIgnoreCase);
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
            ExposedRoute = NormalizeRoute(entry.ExposedRoute),
            TargetUrl = NormalizeTargetUrl(entry.TargetUrl),
            Enabled = entry.Enabled,
            TelemetryEnabled = entry.TelemetryEnabled
        };
    }
}


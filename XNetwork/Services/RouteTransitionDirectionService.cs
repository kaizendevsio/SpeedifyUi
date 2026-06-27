using Microsoft.AspNetCore.Components;

namespace XNetwork.Services;

public sealed class RouteTransitionDirectionService
{
    private readonly Lock _lock = new();
    private string? _currentPath;

    public bool IsBackwards { get; private set; }

    public void Initialize(string uri)
    {
        var path = NormalizePath(uri);

        lock (_lock)
        {
            _currentPath ??= path;
        }
    }

    public void NotifyNavigated(string uri)
    {
        var nextPath = NormalizePath(uri);

        lock (_lock)
        {
            if (_currentPath is null)
            {
                _currentPath = nextPath;
                IsBackwards = false;
                return;
            }

            var currentOrder = GetRouteOrder(_currentPath);
            var nextOrder = GetRouteOrder(nextPath);

            if (currentOrder != nextOrder)
            {
                IsBackwards = nextOrder < currentOrder;
            }

            _currentPath = nextPath;
        }
    }

    public static int GetRouteOrder(string uriOrPath)
    {
        var path = NormalizePath(uriOrPath);

        return path switch
        {
            "/" => 0,
            "/live" => 1,
            "/details" => 2,
            "/analytics" => 2,
            "/xrouter" => 3,
            "/wifi" => 3,
            "/settings" => 4,
            "/xbond" => 4,
            _ => 0
        };
    }

    private static string NormalizePath(string uriOrPath)
    {
        if (uriOrPath.Contains("://", StringComparison.Ordinal) &&
            Uri.TryCreate(uriOrPath, UriKind.Absolute, out var uri))
        {
            return NormalizePath(uri.PathAndQuery);
        }

        var path = uriOrPath.Split('?', '#')[0];
        if (string.IsNullOrWhiteSpace(path))
        {
            return "/";
        }

        path = path.Trim();
        if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }

        return path.TrimEnd('/').ToLowerInvariant() switch
        {
            "" => "/",
            var normalized => normalized
        };
    }
}

using XNetwork.Models;

namespace XNetwork.Utils;

public static class StarlinkAdapterDetector
{
    public static bool HasStarlinkMetadata(Adapter adapter, IEnumerable<string>? adapterNameHints = null)
    {
        var hints = adapterNameHints?.Where(h => !string.IsNullOrWhiteSpace(h)).ToArray() ?? Array.Empty<string>();

        return ContainsStarlink(adapter.Isp) ||
               ContainsStarlink(adapter.Name) ||
               ContainsStarlink(adapter.Type) ||
               ContainsStarlink(adapter.IspType) ||
               string.Equals(adapter.IspType, "Satellite", StringComparison.OrdinalIgnoreCase) ||
               hints.Any(hint =>
                   ContainsHint(adapter.Isp, hint) ||
                   ContainsHint(adapter.Name, hint) ||
                   ContainsHint(adapter.Type, hint) ||
                   ContainsHint(adapter.IspType, hint));
    }

    public static bool MatchesManagementGateway(string? gateway, string? managementHost)
    {
        return !string.IsNullOrWhiteSpace(gateway) &&
               !string.IsNullOrWhiteSpace(managementHost) &&
               string.Equals(gateway.Trim(), managementHost.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public static bool HasStarlinkMetadata(XBondPathStatsSnapshot path, IEnumerable<string>? adapterNameHints = null)
    {
        var hints = adapterNameHints?.Where(h => !string.IsNullOrWhiteSpace(h)).ToArray() ?? Array.Empty<string>();

        return ContainsStarlink(path.Name) ||
               ContainsStarlink(path.InterfaceName) ||
               hints.Any(hint =>
                   ContainsHint(path.Name, hint) ||
                   ContainsHint(path.InterfaceName, hint));
    }

    private static bool ContainsStarlink(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               value.Contains("Starlink", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsHint(string? value, string hint)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               value.Contains(hint, StringComparison.OrdinalIgnoreCase);
    }
}

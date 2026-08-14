using XNetwork.Models;

namespace XNetwork.Services;

/// <summary>
/// Explains why a configured uLink path is dead, so the dashboard can distinguish an operator choice or
/// missing hardware from a genuine outage. A path that is merely down or NO-CARRIER stays
/// <see cref="XBondPathAvailability.Normal"/>: a dish or modem that lost carrier is a fault worth showing.
/// </summary>
public static class XBondPathAvailabilityResolver
{
    public static XBondPathAvailability Resolve(
        string? interfaceName,
        IReadOnlyCollection<string> presentInterfaces,
        IReadOnlyCollection<string> wifiDisabledInterfaces)
    {
        if (string.IsNullOrWhiteSpace(interfaceName))
        {
            return XBondPathAvailability.Normal;
        }

        var name = interfaceName.Trim();

        // Checked first: a disabled adapter that NetworkManager released can also look absent, and
        // "you turned this off" is the more useful explanation.
        if (Contains(wifiDisabledInterfaces, name))
        {
            return XBondPathAvailability.WifiDisabled;
        }

        // The empty guard matters: the interface list is empty on non-Linux hosts and when nmcli fails,
        // and without it every path would be reported as missing hardware.
        if (presentInterfaces.Count > 0 && !Contains(presentInterfaces, name))
        {
            return XBondPathAvailability.InterfaceMissing;
        }

        return XBondPathAvailability.Normal;
    }

    private static bool Contains(IReadOnlyCollection<string> values, string name) =>
        values.Any(value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
}

namespace XNetwork.Services;

/// <summary>
/// Resolves the display name for one network adapter. Precedence is manual alias, then the ISP
/// discovered by <see cref="AdapterIdentityService"/>, then NetworkManager/modem metadata, then the
/// runtime path name, then the raw interface name.
/// </summary>
public static class AdapterNameResolver
{
    public static string Resolve(
        string interfaceName,
        IReadOnlyDictionary<string, string> aliases,
        IReadOnlyDictionary<string, string> ispNames,
        IReadOnlyDictionary<string, string> metadataNames,
        string? runtimeName)
    {
        return Lookup(aliases, interfaceName) ??
               Lookup(ispNames, interfaceName) ??
               Lookup(metadataNames, interfaceName) ??
               Trimmed(runtimeName) ??
               Trimmed(interfaceName) ??
               "";
    }

    private static string? Lookup(IReadOnlyDictionary<string, string>? source, string interfaceName)
    {
        if (source is null || string.IsNullOrWhiteSpace(interfaceName))
        {
            return null;
        }

        return source.TryGetValue(interfaceName, out var value) ? Trimmed(value) : null;
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

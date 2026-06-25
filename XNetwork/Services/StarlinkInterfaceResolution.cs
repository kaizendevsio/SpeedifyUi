namespace XNetwork.Services;

public sealed record StarlinkInterfaceResolution(bool IsAvailable, string? InterfaceName, string Reason)
{
    public static StarlinkInterfaceResolution Available(string interfaceName, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(interfaceName);
        return new StarlinkInterfaceResolution(true, interfaceName, reason);
    }

    public static StarlinkInterfaceResolution Unavailable(string reason)
    {
        return new StarlinkInterfaceResolution(false, null, reason);
    }
}

public interface IStarlinkInterfaceResolver
{
    Task<StarlinkInterfaceResolution> ResolveAsync(CancellationToken cancellationToken = default);
}

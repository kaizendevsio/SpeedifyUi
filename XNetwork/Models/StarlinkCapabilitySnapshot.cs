namespace XNetwork.Models;

public sealed record StarlinkCapabilitySnapshot
{
    public bool IsAvailable { get; init; }

    public DateTimeOffset? LastUpdatedUtc { get; init; }

    public string? Error { get; init; }

    public IReadOnlyList<StarlinkCapability> Capabilities { get; init; } = Array.Empty<StarlinkCapability>();

    public static StarlinkCapabilitySnapshot Unavailable(string? error = null)
    {
        return new StarlinkCapabilitySnapshot
        {
            IsAvailable = false,
            Error = error
        };
    }
}

public sealed record StarlinkCapability
{
    public required string Key { get; init; }

    public required string Label { get; init; }

    public required string Description { get; init; }

    public bool IsDetected { get; init; }

    public bool IsActionable { get; init; }

    public bool IsDisruptive { get; init; }

    public string? ActionUrl { get; init; }

    public string? DisabledReason { get; init; }
}

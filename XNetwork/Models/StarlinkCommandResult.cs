namespace XNetwork.Models;

public sealed record StarlinkCommandResult
{
    public required string Command { get; init; }

    public bool Success { get; init; }

    public required string Message { get; init; }

    public DateTimeOffset CompletedUtc { get; init; } = DateTimeOffset.UtcNow;
}

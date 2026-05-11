namespace XNetwork.Models;

public class NetworkMonitorStatus
{
    public bool IsEnabled { get; init; }

    public int DownTimeoutSeconds { get; init; }

    public IReadOnlyList<NetworkLinkMonitorStatus> Links { get; init; } = [];
}

public class NetworkLinkMonitorStatus
{
    public string Name { get; init; } = "";

    public string? State { get; init; }

    public DateTime? DisconnectedSinceUtc { get; init; }

    public DateTime? RestartSuppressedUntilUtc { get; init; }

    public DateTime? LastRestartAttemptUtc { get; init; }

    public string? LastRestartError { get; init; }

    public int RestartAttemptsInLastHour { get; init; }

    public bool IsDown => DisconnectedSinceUtc.HasValue || string.Equals(State, "disconnected", StringComparison.OrdinalIgnoreCase);

    public bool IsRestartSuppressed => RestartSuppressedUntilUtc.HasValue && RestartSuppressedUntilUtc.Value > DateTime.UtcNow;
}

namespace XNetwork.Models;

public class PrivateReconnectStatus
{
    public bool IsEnabled { get; set; }

    public bool IsRunning { get; set; }

    public int IntervalMinutes { get; set; } = 30;

    public DateTime? LastAttemptUtc { get; set; }

    public DateTime? LastSuccessUtc { get; set; }

    public DateTime? NextAttemptUtc { get; set; }

    public string? LastState { get; set; }

    public string? LastError { get; set; }

    public string Message { get; set; } = "Private reconnect is disabled";

    public List<PrivateReconnectEvent> RecentEvents { get; set; } = new();
}

public class PrivateReconnectEvent
{
    public DateTime TimestampUtc { get; set; }

    public string Message { get; set; } = "";

    public bool IsError { get; set; }
}

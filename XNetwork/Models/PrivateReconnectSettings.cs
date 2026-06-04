namespace XNetwork.Models;

public class PrivateReconnectSettings
{
    public bool Enabled { get; set; }

    public int IntervalMinutes { get; set; } = 30;

    public int DelaySeconds { get; set; } = 2;
}

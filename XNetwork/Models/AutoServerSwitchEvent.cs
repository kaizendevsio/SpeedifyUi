namespace XNetwork.Models;

public class AutoServerSwitchEvent
{
    public DateTime TimestampUtc { get; set; }

    public string Type { get; set; } = "Info";

    public string Message { get; set; } = "";

    public string? Server { get; set; }

    public double? Score { get; set; }
}

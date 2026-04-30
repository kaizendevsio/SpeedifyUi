namespace XNetwork.Models;

public class ProbeScoresResponse
{
    public DateTime GeneratedUtc { get; set; }

    public DateTime? LastRunUtc { get; set; }

    public DateTime? LastCompletedUtc { get; set; }

    public string? LastError { get; set; }

    public List<ServerHealthScore> Scores { get; set; } = new();
}

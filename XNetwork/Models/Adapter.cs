namespace XNetwork.Models;

public record Adapter(
    string AdapterId,
    string Name,
    string Isp,
    string State,
    string Priority,
    string WorkingPriority,
    string Type);
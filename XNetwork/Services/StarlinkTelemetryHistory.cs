using XNetwork.Models;

namespace XNetwork.Services;

public sealed class StarlinkTelemetryHistory
{
    private readonly object _lock = new();
    private readonly Queue<StarlinkTelemetrySnapshot> _samples = new();

    public void Add(StarlinkTelemetrySnapshot snapshot, TimeSpan maxAge, int maxSamples, DateTimeOffset now)
    {
        if (snapshot.LastUpdatedUtc is null)
        {
            return;
        }

        lock (_lock)
        {
            _samples.Enqueue(snapshot);
            Prune(maxAge, Math.Max(1, maxSamples), now);
        }
    }

    public IReadOnlyList<StarlinkTelemetrySnapshot> GetSamples()
    {
        lock (_lock)
        {
            return _samples.ToList();
        }
    }

    private void Prune(TimeSpan maxAge, int maxSamples, DateTimeOffset now)
    {
        while (_samples.Count > 0 &&
               (now - _samples.Peek().LastUpdatedUtc!.Value > maxAge || _samples.Count > maxSamples))
        {
            _samples.Dequeue();
        }
    }
}

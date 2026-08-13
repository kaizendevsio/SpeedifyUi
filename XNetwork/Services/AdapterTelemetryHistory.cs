using XNetwork.Models;

namespace XNetwork.Services;

/// <summary>
/// Bounded per-interface metric history backing the adapter details charts. Bounded by both age and
/// sample count so the charts always show a fixed, recent window rather than everything since start.
/// </summary>
public sealed class AdapterTelemetryHistory
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Queue<AdapterTelemetrySample>> _samples = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _maxSamples;
    private readonly TimeSpan _maxAge;

    public AdapterTelemetryHistory()
        : this(DefaultMaxSamples, DefaultMaxAge)
    {
    }

    public AdapterTelemetryHistory(int maxSamples)
        : this(maxSamples, DefaultMaxAge)
    {
    }

    public AdapterTelemetryHistory(int maxSamples, TimeSpan maxAge)
    {
        _maxSamples = Math.Max(1, maxSamples);
        _maxAge = maxAge > TimeSpan.Zero ? maxAge : DefaultMaxAge;
    }

    /// <summary>Fifteen minutes of one-second samples.</summary>
    public static TimeSpan DefaultMaxAge { get; } = TimeSpan.FromMinutes(15);

    public const int DefaultMaxSamples = 900;

    public void Add(string interfaceName, AdapterTelemetrySample sample)
    {
        if (string.IsNullOrWhiteSpace(interfaceName))
        {
            return;
        }

        lock (_lock)
        {
            if (!_samples.TryGetValue(interfaceName, out var queue))
            {
                queue = new Queue<AdapterTelemetrySample>();
                _samples[interfaceName] = queue;
            }

            queue.Enqueue(sample);
            Prune(queue, sample.TimestampUtc);
        }
    }

    public IReadOnlyList<AdapterTelemetrySample> GetSamples(string interfaceName)
    {
        if (string.IsNullOrWhiteSpace(interfaceName))
        {
            return [];
        }

        lock (_lock)
        {
            return _samples.TryGetValue(interfaceName, out var queue) ? queue.ToList() : [];
        }
    }

    private void Prune(Queue<AdapterTelemetrySample> queue, DateTimeOffset now)
    {
        while (queue.Count > 0 &&
               (queue.Count > _maxSamples || now - queue.Peek().TimestampUtc > _maxAge))
        {
            queue.Dequeue();
        }
    }

    public void EvictMissing(IEnumerable<string> presentInterfaces)
    {
        var present = presentInterfaces.ToHashSet(StringComparer.OrdinalIgnoreCase);
        lock (_lock)
        {
            foreach (var device in _samples.Keys.Where(device => !present.Contains(device)).ToArray())
            {
                _samples.Remove(device);
            }
        }
    }
}

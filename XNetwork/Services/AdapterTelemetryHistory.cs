using XNetwork.Models;

namespace XNetwork.Services;

/// <summary>Bounded per-interface metric history backing the adapter details charts.</summary>
public sealed class AdapterTelemetryHistory(int maxSamples = 300)
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Queue<AdapterTelemetrySample>> _samples = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _maxSamples = Math.Max(1, maxSamples);

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
            while (queue.Count > _maxSamples)
            {
                queue.Dequeue();
            }
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

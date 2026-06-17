using XNetwork.Models;

namespace XNetwork.Services;

public sealed class XBondSnapshotCache(
    IXBondStatsProvider statsProvider,
    TimeProvider? timeProvider = null,
    TimeSpan? cacheDuration = null)
{
    private static readonly TimeSpan DefaultCacheDuration = TimeSpan.FromMilliseconds(500);

    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly TimeSpan _cacheDuration = cacheDuration ?? DefaultCacheDuration;
    private XBondStatsSnapshot? _cachedSnapshot;
    private DateTimeOffset _expiresAtUtc = DateTimeOffset.MinValue;

    public async Task<XBondStatsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        if (_cachedSnapshot is not null && now < _expiresAtUtc)
        {
            return _cachedSnapshot;
        }

        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = _timeProvider.GetUtcNow();
            if (_cachedSnapshot is not null && now < _expiresAtUtc)
            {
                return _cachedSnapshot;
            }

            _cachedSnapshot = await statsProvider.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            _expiresAtUtc = now + _cacheDuration;
            return _cachedSnapshot;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public void Invalidate()
    {
        _expiresAtUtc = DateTimeOffset.MinValue;
    }
}

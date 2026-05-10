namespace Smited.Daemon.Pishock.Internal;

/// <summary>
/// Per-descriptor rate limiter. Each <see cref="TryConsume"/> attempt
/// either succeeds and removes one token, or fails because the bucket
/// is empty. Tokens accrue at <c>refillPerSecond</c> and saturate at
/// <c>capacity</c>.
/// </summary>
/// <remarks>
/// <para>
/// All time reads go through <see cref="TimeProvider.GetTimestamp"/> +
/// <see cref="TimeProvider.GetElapsedTime(long, long)"/>, so a
/// <c>FakeTimeProvider</c> handed in by tests can fast-forward the
/// clock and exercise the refill logic without <c>Thread.Sleep</c>.
/// </para>
/// <para>
/// A simple <c>lock</c> is sufficient: contention on a single shocker's
/// bucket is non-existent in practice — at the default refill rate of
/// 1 op/s the lock is held for sub-microsecond intervals between
/// trigger calls that are themselves seconds apart.
/// </para>
/// </remarks>
internal sealed class TokenBucket
{
    private readonly int _capacity;
    private readonly double _refillPerSecond;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private double _tokens;
    private long _lastRefillTimestamp;

    public TokenBucket(int capacity, int refillPerSecond, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(refillPerSecond, 1);

        _capacity = capacity;
        _refillPerSecond = refillPerSecond;
        _time = time;
        _tokens = capacity;
        _lastRefillTimestamp = time.GetTimestamp();
    }

    public bool TryConsume()
    {
        lock (_lock)
        {
            Refill();
            if (_tokens >= 1.0)
            {
                _tokens -= 1.0;
                return true;
            }
            return false;
        }
    }

    private void Refill()
    {
        var now = _time.GetTimestamp();
        var elapsed = _time.GetElapsedTime(_lastRefillTimestamp, now);
        var refilled = elapsed.TotalSeconds * _refillPerSecond;
        if (refilled > 0)
        {
            _tokens = Math.Min(_capacity, _tokens + refilled);
            _lastRefillTimestamp = now;
        }
    }
}

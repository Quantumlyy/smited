namespace Smited.Daemon.Triggering;

/// <summary>
/// In-memory <see cref="IBreakerService"/> implementation. Lock-protects
/// the mutable state and snapshots a <see cref="BreakerState"/> under
/// the lock before invoking <see cref="StateChanged"/> outside it, so
/// subscribers cannot deadlock by re-entering the breaker.
/// </summary>
/// <remarks>
/// Breaker state does NOT survive a daemon restart — that's the v1
/// scope decision (see <c>docs/admin.md</c>). A future PR could persist
/// the state to disk if the user calls for it, but the dominant case
/// (panic followed by a short investigation, then re-arm) is well-served
/// by in-memory.
/// </remarks>
internal sealed class BreakerService : IBreakerService
{
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private bool _tripped;
    private DateTimeOffset? _trippedAt;
    private string? _reason;

    public BreakerService(TimeProvider time) => _time = time;

    public bool IsTripped { get { lock (_lock) { return _tripped; } } }
    public DateTimeOffset? TrippedAt { get { lock (_lock) { return _trippedAt; } } }
    public string? TripReason { get { lock (_lock) { return _reason; } } }

    public event Action<BreakerState>? StateChanged;

    public void Trip(string reason)
    {
        BreakerState snapshot;
        lock (_lock)
        {
            _tripped = true;
            _trippedAt = _time.GetUtcNow();
            _reason = reason;
            snapshot = new BreakerState(true, _trippedAt, _reason);
        }
        StateChanged?.Invoke(snapshot);
    }

    public void Rearm()
    {
        BreakerState? snapshot = null;
        lock (_lock)
        {
            // Re-arm on an untripped breaker is a no-op; suppressing the
            // StateChanged invocation here means UI subscribers don't
            // see a redundant "untripped -> untripped" tick on startup
            // when an admin pre-emptively clicks the dialog.
            if (!_tripped)
            {
                return;
            }
            _tripped = false;
            _trippedAt = null;
            _reason = null;
            snapshot = new BreakerState(false, null, null);
        }
        StateChanged?.Invoke(snapshot);
    }
}

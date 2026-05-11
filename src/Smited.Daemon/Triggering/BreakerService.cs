using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
    private readonly ILogger<BreakerService> _logger;
    private readonly object _lock = new();
    private bool _tripped;
    private DateTimeOffset? _trippedAt;
    private string? _reason;

    public BreakerService(TimeProvider time, ILogger<BreakerService>? logger = null)
    {
        _time = time;
        _logger = logger ?? NullLogger<BreakerService>.Instance;
    }

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
        RaiseStateChanged(snapshot);
    }

    public void Rearm()
    {
        BreakerState snapshot;
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
        RaiseStateChanged(snapshot);
    }

    /// <summary>
    /// Invokes each <see cref="StateChanged"/> subscriber under an
    /// individual try/catch. A throwing subscriber MUST NOT prevent the
    /// breaker's state change from reaching other subscribers, nor
    /// propagate back to the caller. Critical because
    /// <c>SmitedActionService.PanicAsync</c> calls <see cref="Trip"/>
    /// before <c>StopAsync</c>: a faulty UI subscriber (Header.razor,
    /// SensationTester.razor) would otherwise let the trip-side effect
    /// throw, skip the stop, and leave active sensations playing.
    /// </summary>
    private void RaiseStateChanged(BreakerState snapshot)
    {
        var handlers = StateChanged;
        if (handlers is null)
        {
            return;
        }
        foreach (Action<BreakerState> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(snapshot);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "BreakerService subscriber threw on StateChanged; continuing with remaining subscribers");
            }
        }
    }
}

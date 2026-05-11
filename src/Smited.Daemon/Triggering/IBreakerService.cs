namespace Smited.Daemon.Triggering;

/// <summary>
/// Daemon-wide circuit breaker. When tripped, all <c>Trigger</c> calls
/// reject (the rejection rides on
/// <see cref="Smited.V1.TriggerErrorCode.BackendUnavailable"/> with a
/// <c>BREAKER_TRIPPED:</c> message prefix — wire schema is pinned at
/// <c>buf.build/quantumly-labs/smited:v0.1.0</c>, so adding a dedicated
/// enum value is out of scope for this PR). Other surfaces (Stop, panic
/// HTTP, status reads, history, events) are unaffected — the breaker
/// exists to block new sensations after a panic, not to take the
/// daemon offline. Re-arm via <see cref="IBreakerChallengeService"/>.
/// </summary>
internal interface IBreakerService
{
    bool IsTripped { get; }
    DateTimeOffset? TrippedAt { get; }
    string? TripReason { get; }

    /// <summary>
    /// Latch the breaker. Idempotent — calling Trip on an
    /// already-tripped breaker updates the reason+time only if the new
    /// trip is more recent.
    /// </summary>
    void Trip(string reason);

    /// <summary>
    /// Reset the breaker to untripped. Should only be called after
    /// challenge verification succeeds.
    /// </summary>
    void Rearm();

    /// <summary>
    /// Subscribe to breaker state changes. Used by the admin UI to
    /// show banner updates without polling.
    /// </summary>
    event Action<BreakerState>? StateChanged;
}

/// <summary>
/// Snapshot of <see cref="IBreakerService"/> state, captured under the
/// breaker's lock so subscribers see a coherent view.
/// </summary>
internal sealed record BreakerState(
    bool IsTripped,
    DateTimeOffset? TrippedAt,
    string? TripReason);

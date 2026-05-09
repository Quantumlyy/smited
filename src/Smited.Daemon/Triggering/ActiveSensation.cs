namespace Smited.Daemon.Triggering;

/// <summary>
/// Coordinator-side handle for an in-flight sensation: the backend already
/// has its own internal task running the sensation; this record carries
/// the cross-cutting state the coordinator needs (cancellation token,
/// priority for preemption, start time for CANCEL_OLDEST ordering, etc.).
/// </summary>
internal sealed class ActiveSensation
{
    private int _released;

    public ActiveSensation(
        string sensationId,
        string backendId,
        int priority,
        DateTimeOffset startedAt,
        string clientTraceId,
        CancellationToken outerToken)
    {
        SensationId = sensationId;
        BackendId = backendId;
        Priority = priority;
        StartedAt = startedAt;
        ClientTraceId = clientTraceId;
        Cts = CancellationTokenSource.CreateLinkedTokenSource(outerToken);
    }

    public string SensationId { get; }

    public string BackendId { get; }

    public int Priority { get; }

    public DateTimeOffset StartedAt { get; }

    public string ClientTraceId { get; }

    public CancellationTokenSource Cts { get; }

    /// <summary>
    /// Marks this sensation as released exactly once. Returns true on the
    /// first call, false on subsequent calls — used so concurrent paths
    /// (preemption, natural completion, explicit stop) don't double-release
    /// the concurrency slot.
    /// </summary>
    public bool TryMarkReleased() =>
        Interlocked.CompareExchange(ref _released, 1, 0) == 0;
}

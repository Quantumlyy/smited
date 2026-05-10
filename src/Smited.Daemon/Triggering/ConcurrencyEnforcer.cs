using System.Collections.Concurrent;
using Smited.Daemon.Backends;
using Smited.V1;

namespace Smited.Daemon.Triggering;

/// <summary>
/// Per-backend admission control. Implements the four concurrency policies
/// described in the schema:
/// <list type="bullet">
///   <item><c>REJECT_NEW</c> — drop the new candidate when at capacity.</item>
///   <item><c>CANCEL_OLDEST</c> — preempt the earliest-started sensation when at capacity.</item>
///   <item><c>PRIORITY</c> — preempt the lowest-priority active sensation if its priority is below the candidate's; otherwise reject.</item>
///   <item><c>QUEUE</c> — wait for a slot to free up.</item>
/// </list>
/// </summary>
internal sealed class ConcurrencyEnforcer
{
    private readonly ConcurrentDictionary<string, BackendState> _state =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task<ConcurrencyDecision> AdmitAsync(
        IHapticBackend backend,
        ActiveSensation candidate,
        CancellationToken ct)
    {
        var state = _state.GetOrAdd(backend.Id, _ => CreateState(backend));

        return state.Model.Policy switch
        {
            ConcurrencyPolicy.RejectNew => RejectNew(state, candidate),
            ConcurrencyPolicy.CancelOldest => CancelOldest(state, candidate),
            ConcurrencyPolicy.Priority => Priority(state, candidate),
            ConcurrencyPolicy.Queue => await Queue(state, candidate, ct),
            _ => new ConcurrencyDecision.Reject($"Unknown concurrency policy: {state.Model.Policy}"),
        };
    }

    public void Release(string backendId, ActiveSensation sensation)
    {
        if (!_state.TryGetValue(backendId, out var state))
        {
            return;
        }

        bool removed;
        lock (state.Sync)
        {
            removed = state.Active.Remove(sensation);
        }
        if (removed)
        {
            state.Slots?.Release();
        }
    }

    private static BackendState CreateState(IHapticBackend backend)
    {
        var max = (int)Math.Max(1u, backend.Concurrency.MaxConcurrent);
        return new BackendState(
            backend.Concurrency,
            backend.Concurrency.Policy == ConcurrencyPolicy.Queue
                ? new SemaphoreSlim(max, max)
                : null);
    }

    private static ConcurrencyDecision RejectNew(BackendState state, ActiveSensation candidate)
    {
        lock (state.Sync)
        {
            if (state.Active.Count >= state.Model.MaxConcurrent)
            {
                return new ConcurrencyDecision.Reject(
                    $"max_concurrent={state.Model.MaxConcurrent} reached (REJECT_NEW)");
            }
            state.Active.Add(candidate);
            return new ConcurrencyDecision.Admit();
        }
    }

    private static ConcurrencyDecision CancelOldest(BackendState state, ActiveSensation candidate)
    {
        lock (state.Sync)
        {
            if (state.Active.Count < state.Model.MaxConcurrent)
            {
                state.Active.Add(candidate);
                return new ConcurrencyDecision.Admit();
            }

            var oldest = state.Active.MinBy(s => s.StartedAt)!;
            state.Active.Remove(oldest);
            state.Active.Add(candidate);
            return new ConcurrencyDecision.Preempt(new[] { oldest });
        }
    }

    private static ConcurrencyDecision Priority(BackendState state, ActiveSensation candidate)
    {
        lock (state.Sync)
        {
            if (state.Active.Count < state.Model.MaxConcurrent)
            {
                state.Active.Add(candidate);
                return new ConcurrencyDecision.Admit();
            }

            var lowest = state.Active.MinBy(s => s.Priority);
            if (lowest is null || lowest.Priority >= candidate.Priority)
            {
                return new ConcurrencyDecision.Reject(
                    $"All active sensations have priority >= {candidate.Priority} (PRIORITY)");
            }

            state.Active.Remove(lowest);
            state.Active.Add(candidate);
            return new ConcurrencyDecision.Preempt(new[] { lowest });
        }
    }

    private static async Task<ConcurrencyDecision> Queue(
        BackendState state,
        ActiveSensation candidate,
        CancellationToken ct)
    {
        await state.Slots!.WaitAsync(ct).ConfigureAwait(false);

        lock (state.Sync)
        {
            state.Active.Add(candidate);
        }
        return new ConcurrencyDecision.Admit();
    }

    private sealed class BackendState
    {
        public BackendState(ConcurrencyModel model, SemaphoreSlim? slots)
        {
            Model = model;
            Slots = slots;
        }

        public ConcurrencyModel Model { get; }

        public List<ActiveSensation> Active { get; } = new();

        public SemaphoreSlim? Slots { get; }

        public Lock Sync { get; } = new();
    }
}

internal abstract record ConcurrencyDecision
{
    public sealed record Admit : ConcurrencyDecision;

    public sealed record Reject(string Reason) : ConcurrencyDecision;

    public sealed record Preempt(IReadOnlyList<ActiveSensation> ToCancel) : ConcurrencyDecision;
}

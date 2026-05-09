using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Smited.Daemon.Backends;
using Smited.Daemon.Backends.Internal;
using Smited.Daemon.Sensations;
using Smited.V1;
using ParameterValue = Smited.Daemon.Backends.Internal.ParameterValue;
using MicrosensationParameters = Smited.Daemon.Backends.Internal.MicrosensationParameters;

namespace Smited.Daemon.Triggering;

/// <summary>
/// Mediates between the gRPC service and backends. Resolves the target
/// backend, looks up named sensations, validates zones and parameters,
/// applies the backend's concurrency policy, dispatches to the backend,
/// and tracks active sensations so <see cref="StopAsync"/> can cancel them.
/// </summary>
internal sealed class TriggerCoordinator
{
    private readonly BackendRegistry _registry;
    private readonly SensationLibrary _library;
    private readonly ConcurrencyEnforcer _concurrency;
    private readonly TimeProvider _time;
    private readonly ILogger<TriggerCoordinator> _logger;

    private readonly ConcurrentDictionary<string, ActiveSensation> _active =
        new(StringComparer.OrdinalIgnoreCase);

    public TriggerCoordinator(
        BackendRegistry registry,
        SensationLibrary library,
        ConcurrencyEnforcer concurrency,
        TimeProvider time,
        ILogger<TriggerCoordinator> logger)
    {
        _registry = registry;
        _library = library;
        _concurrency = concurrency;
        _time = time;
        _logger = logger;
    }

    public async Task<TriggerOutcome> TriggerAsync(
        ResolvedTriggerInput input,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(input.BackendId))
        {
            return Reject(input, TriggerErrorCode.BackendNotFound,
                "backend_id is required (auto-routing is the multiplexer's job, not the daemon's)",
                "backend_id");
        }

        var backend = _registry.TryGet(input.BackendId);
        if (backend is null)
        {
            return Reject(input, TriggerErrorCode.BackendNotFound,
                $"backend '{input.BackendId}' is not registered",
                "backend_id");
        }

        var resolution = ResolveSensation(input, backend);
        if (resolution.Rejection is not null)
        {
            return resolution.Rejection;
        }

        var validation = ValidateAgainstBackend(backend, resolution.ZoneIds, resolution.Microsensations);
        if (validation is not null)
        {
            return Reject(input, validation.Value.Code, validation.Value.Message, validation.Value.Field);
        }

        var sensationId = Guid.NewGuid().ToString("N")[..16];
        var active = new ActiveSensation(
            sensationId,
            backend.Id,
            input.Priority,
            _time.GetUtcNow(),
            input.ClientTraceId,
            ct);

        var decision = await _concurrency.AdmitAsync(backend, active, ct).ConfigureAwait(false);
        switch (decision)
        {
            case ConcurrencyDecision.Reject reject:
                return Reject(input, TriggerErrorCode.RateLimited, reject.Reason, "concurrency");

            case ConcurrencyDecision.Preempt preempt:
                foreach (var preempted in preempt.ToCancel)
                {
                    _logger.LogInformation(
                        "Preempting sensation {PreemptedId} on backend {BackendId} for {NewId}",
                        preempted.SensationId, backend.Id, sensationId);
                    if (preempted.TryMarkReleased())
                    {
                        _active.TryRemove(preempted.SensationId, out _);
                        try
                        {
                            await preempted.Cts.CancelAsync().ConfigureAwait(false);
                        }
                        catch (ObjectDisposedException)
                        {
                            // Already disposed elsewhere; nothing to do.
                        }
                        // Preempt path takes ownership of release: the monitoring
                        // task's ReleaseInternal short-circuits on TryMarkReleased,
                        // so dispose the CTS here.
                        preempted.Cts.Dispose();
                    }
                }
                break;
        }

        _active[sensationId] = active;

        var resolvedIntensity = input.IntensityScale ?? resolution.DefaultIntensity;
        var request = new BackendTriggerRequest(
            sensationId,
            input.SensationName,
            resolution.ZoneIds,
            resolvedIntensity,
            input.Priority,
            input.ClientTraceId,
            resolution.Microsensations);

        BackendTriggerResult result;
        try
        {
            result = await backend.TriggerAsync(request, active.Cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Backend {BackendId} threw while accepting trigger {SensationId}",
                backend.Id, sensationId);
            ReleaseInternal(active);
            return Reject(input, TriggerErrorCode.Internal,
                $"backend threw: {ex.Message}", null);
        }

        ScheduleSlotRelease(backend.Id, active, result.EstimatedDuration);
        return new TriggerOutcome.Accepted(
            input.ClientTraceId,
            sensationId,
            resolution.ZoneIds,
            resolvedIntensity);
    }

    public async Task<int> StopAsync(BackendStopRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.All)
        {
            return await StopMatching(_ => true, ct).ConfigureAwait(false);
        }

        if (!string.IsNullOrEmpty(request.SensationId))
        {
            return await StopMatching(s => s.SensationId == request.SensationId, ct).ConfigureAwait(false);
        }

        return 0;
    }

    /// <summary>
    /// Stops every active sensation owned by a specific backend. Used by
    /// the gRPC <c>Stop{backend_id:...}</c> oneof case.
    /// </summary>
    public Task<int> StopBackendAsync(string backendId, CancellationToken ct) =>
        StopMatching(s => string.Equals(s.BackendId, backendId, StringComparison.OrdinalIgnoreCase), ct);

    private async Task<int> StopMatching(Func<ActiveSensation, bool> predicate, CancellationToken ct)
    {
        var matched = _active.Values.Where(predicate).ToList();
        var stopped = 0;

        foreach (var active in matched)
        {
            if (!active.TryMarkReleased())
            {
                continue;
            }
            _active.TryRemove(active.SensationId, out _);

            // Ask the backend to stop this sensation, then cancel our token
            // so any local monitoring task wakes up.
            var backend = _registry.TryGet(active.BackendId);
            if (backend is not null)
            {
                try
                {
                    await backend.StopAsync(
                        new BackendStopRequest(active.SensationId, All: false),
                        ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Backend {BackendId} threw while stopping sensation {SensationId}",
                        active.BackendId, active.SensationId);
                }
            }

            try
            {
                await active.Cts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException) { }

            _concurrency.Release(active.BackendId, active);
            // Stop short-circuits TryMarkReleased so the monitoring task's
            // ReleaseInternal becomes a no-op; dispose the CTS here instead.
            active.Cts.Dispose();
            stopped++;
        }

        return stopped;
    }

    private void ScheduleSlotRelease(string backendId, ActiveSensation active, TimeSpan after)
    {
        // The backend has its own internal task driving the playback and
        // emitting Started/Completed events. We mirror its timing here so
        // the concurrency slot is released at the right moment without
        // needing to bolt onto the event stream from inside the coordinator.
        _ = Task.Run(async () =>
        {
            try
            {
                if (after > TimeSpan.Zero)
                {
                    await Task.Delay(after, _time, active.Cts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Preempted or stopped — fall through to release.
            }
            finally
            {
                ReleaseInternal(active);
            }
        });
    }

    private void ReleaseInternal(ActiveSensation active)
    {
        if (!active.TryMarkReleased())
        {
            return;
        }
        _active.TryRemove(active.SensationId, out _);
        _concurrency.Release(active.BackendId, active);
        // Dispose the linked CTS we created when admitting this sensation
        // so its registration on the caller's token is released.
        active.Cts.Dispose();
    }

    private static TriggerOutcome.Rejected Reject(
        ResolvedTriggerInput input,
        TriggerErrorCode code,
        string message,
        string? field) =>
        new(input.ClientTraceId, code, message, field);

    private SensationResolution ResolveSensation(ResolvedTriggerInput input, IHapticBackend backend)
    {
        if (input.SensationName is { Length: > 0 } name)
        {
            var entry = _library.Get(input.BackendId, name);
            if (entry is null)
            {
                return SensationResolution.FromRejection(
                    Reject(input, TriggerErrorCode.SensationNotFound,
                        $"sensation '{name}' not registered for backend '{input.BackendId}'",
                        "sensation_name"));
            }

            return new SensationResolution(
                Microsensations: entry.Definition,
                ZoneIds: input.ZoneIds.Count > 0 ? input.ZoneIds : entry.DefaultZoneIds,
                DefaultIntensity: entry.DefaultIntensity,
                Rejection: null);
        }

        if (input.InlineMicrosensations is { Count: > 0 } inline)
        {
            return new SensationResolution(
                Microsensations: inline,
                ZoneIds: input.ZoneIds,
                DefaultIntensity: null,
                Rejection: null);
        }

        return SensationResolution.FromRejection(
            Reject(input, TriggerErrorCode.Internal,
                "request has neither sensation_name nor inline microsensations",
                "sensation"));
    }

    private static (TriggerErrorCode Code, string Message, string Field)? ValidateAgainstBackend(
        IHapticBackend backend,
        IReadOnlyList<string> zoneIds,
        IReadOnlyList<MicrosensationParameters> microsensations)
    {
        var knownZones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var z in backend.Zones.Zones)
        {
            knownZones.Add(z.Id);
        }
        foreach (var g in backend.Zones.Groups)
        {
            knownZones.Add(g.Id);
        }
        foreach (var z in zoneIds)
        {
            if (!knownZones.Contains(z))
            {
                return (TriggerErrorCode.InvalidZone,
                    $"zone '{z}' is not present on backend '{backend.Id}'",
                    "zone_ids");
            }
        }

        var paramByName = backend.Parameters.Parameters
            .ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < microsensations.Count; i++)
        {
            var micro = microsensations[i];
            foreach (var (key, value) in micro.Values)
            {
                if (!paramByName.TryGetValue(key, out var def))
                {
                    return (TriggerErrorCode.InvalidParameter,
                        $"parameter '{key}' is not declared by backend '{backend.Id}'",
                        $"microsensations[{i}].parameters.{key}");
                }
                if (!ValueMatchesType(value, def))
                {
                    return (TriggerErrorCode.InvalidParameter,
                        $"parameter '{key}' has wrong value type for declared {def.Type}",
                        $"microsensations[{i}].parameters.{key}");
                }
                if (!ValueWithinRange(value, def, out var rangeError))
                {
                    return (TriggerErrorCode.InvalidParameter,
                        $"parameter '{key}' out of range: {rangeError}",
                        $"microsensations[{i}].parameters.{key}");
                }
            }
            foreach (var def in backend.Parameters.Parameters)
            {
                if (def.Required && !micro.Values.ContainsKey(def.Name))
                {
                    return (TriggerErrorCode.InvalidParameter,
                        $"required parameter '{def.Name}' is missing",
                        $"microsensations[{i}].parameters.{def.Name}");
                }
            }
        }

        return null;
    }

    private static bool ValueMatchesType(ParameterValue value, ParameterDef def) => def.Type switch
    {
        ParameterType.Number => value is ParameterValue.Number,
        ParameterType.Bool => value is ParameterValue.Bool,
        ParameterType.String => value is ParameterValue.Text,
        ParameterType.Duration => value is ParameterValue.Duration,
        ParameterType.Enum => value is ParameterValue.EnumValue,
        _ => false,
    };

    private static bool ValueWithinRange(ParameterValue value, ParameterDef def, out string? error)
    {
        error = null;
        switch (value)
        {
            case ParameterValue.Number n:
                if (def.HasMin && n.Value < def.Min) { error = $"{n.Value} < min {def.Min}"; return false; }
                if (def.HasMax && n.Value > def.Max) { error = $"{n.Value} > max {def.Max}"; return false; }
                break;
            case ParameterValue.Duration d:
                var seconds = d.Value.TotalSeconds;
                if (def.HasMin && seconds < def.Min) { error = $"{seconds}s < min {def.Min}s"; return false; }
                if (def.HasMax && seconds > def.Max) { error = $"{seconds}s > max {def.Max}s"; return false; }
                break;
            case ParameterValue.EnumValue e:
                if (def.EnumValues.Count > 0 && !def.EnumValues.Contains(e.Value))
                {
                    error = $"'{e.Value}' is not in enum_values [{string.Join(", ", def.EnumValues)}]";
                    return false;
                }
                break;
        }
        return true;
    }

    private sealed record SensationResolution(
        IReadOnlyList<MicrosensationParameters> Microsensations,
        IReadOnlyList<string> ZoneIds,
        uint? DefaultIntensity,
        TriggerOutcome.Rejected? Rejection)
    {
        public static SensationResolution FromRejection(TriggerOutcome.Rejected rejection) =>
            new(Array.Empty<MicrosensationParameters>(), Array.Empty<string>(), null, rejection);
    }
}

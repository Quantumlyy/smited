using System.Text.Json;
using Microsoft.Extensions.Logging;
using Smited.Daemon.Backends.Internal;
using Smited.Daemon.History;

namespace Smited.Daemon.Triggering;

/// <summary>
/// In-process facade combining <see cref="TriggerCoordinator"/> calls with
/// their corresponding history records and structured logging. The gRPC
/// service layer, the panic HTTP endpoint, and the Blazor admin UI all
/// dispatch through this so every action is recorded uniformly regardless
/// of which surface initiated it.
/// </summary>
/// <remarks>
/// Calling the coordinator directly is supported (tests do it), but
/// production callers should use this facade. The recording is best-effort:
/// the underlying <see cref="IHistoryRecorder"/> swallows database failures
/// after logging, matching its existing contract — a wedged history sink
/// must never block the haptics hot path.
///
/// History writes are fire-and-forget (<c>_ = _history.Record...</c>) to
/// avoid coupling response latency to history availability, mirroring what
/// the gRPC service did before the facade existed.
/// </remarks>
internal sealed class SmitedActionService
{
    private readonly TriggerCoordinator _coordinator;
    private readonly IHistoryRecorder _history;
    private readonly TimeProvider _time;
    private readonly ILogger<SmitedActionService> _logger;

    public SmitedActionService(
        TriggerCoordinator coordinator,
        IHistoryRecorder history,
        TimeProvider time,
        ILogger<SmitedActionService> logger)
    {
        _coordinator = coordinator;
        _history = history;
        _time = time;
        _logger = logger;
    }

    public async Task<TriggerOutcome> TriggerAsync(
        ResolvedTriggerInput input,
        TriggerSource source,
        CancellationToken ct)
    {
        var outcome = await _coordinator.TriggerAsync(input, ct).ConfigureAwait(false);
        _ = _history.RecordTriggerAsync(BuildTriggerRecord(input, outcome));
        return outcome;
    }

    public async Task<int> StopAsync(
        BackendStopRequest request,
        TriggerSource source,
        CancellationToken ct)
    {
        var stopped = await _coordinator.StopAsync(request, ct).ConfigureAwait(false);
        _ = _history.RecordStopAsync(new StopRecord
        {
            Timestamp = _time.GetUtcNow(),
            Source = SourceToString(source),
            All = request.All,
            SensationId = request.SensationId,
            StoppedCount = stopped,
        });
        return stopped;
    }

    public async Task<int> StopBackendAsync(
        string backendId,
        TriggerSource source,
        CancellationToken ct)
    {
        var stopped = await _coordinator.StopBackendAsync(backendId, ct).ConfigureAwait(false);
        _ = _history.RecordStopAsync(new StopRecord
        {
            Timestamp = _time.GetUtcNow(),
            Source = SourceToString(source),
            All = true,
            BackendId = backendId,
            StoppedCount = stopped,
        });
        return stopped;
    }

    /// <summary>
    /// Cancels every active sensation across every backend and writes the
    /// paired <see cref="PanicRecord"/> + <see cref="StopRecord"/>. Always
    /// emits the <c>Critical</c>-level log line a postmortem needs to find,
    /// regardless of caller (HTTP endpoint, admin button). Rethrows
    /// coordinator failures after recording the failure row so HTTP
    /// callers can surface a 500.
    /// </summary>
    public async Task<int> PanicAsync(
        TriggerSource source,
        string? peer,
        string? userAgent,
        CancellationToken ct)
    {
        var timestamp = _time.GetUtcNow();
        _logger.LogCritical(
            "PANIC stop requested (source={Source}, peer={Peer}, userAgent={UserAgent})",
            source, peer ?? "<n/a>", userAgent ?? "<n/a>");

        int stopped;
        try
        {
            stopped = await _coordinator.StopAsync(
                new BackendStopRequest(SensationId: null, All: true), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex,
                "PANIC stop FAILED (source={Source}); coordinator threw", source);
            _ = _history.RecordPanicAsync(new PanicRecord
            {
                Timestamp = timestamp,
                Peer = peer ?? "",
                UserAgent = userAgent ?? "",
                Ok = false,
                StoppedCount = 0,
                Error = ex.Message,
            });
            throw;
        }

        _logger.LogCritical(
            "PANIC stop completed (source={Source}, stoppedCount={StoppedCount})",
            source, stopped);

        _ = _history.RecordPanicAsync(new PanicRecord
        {
            Timestamp = timestamp,
            Peer = peer ?? "",
            UserAgent = userAgent ?? "",
            Ok = true,
            StoppedCount = stopped,
        });
        _ = _history.RecordStopAsync(new StopRecord
        {
            Timestamp = timestamp,
            Source = SourceToString(source),
            All = true,
            StoppedCount = stopped,
        });

        return stopped;
    }

    private TriggerRecord BuildTriggerRecord(ResolvedTriggerInput input, TriggerOutcome outcome)
    {
        // For accepted triggers prefer the coordinator's resolved zones
        // and intensity (a named sensation may supply defaults the request
        // omitted), so the history row reflects what actually played
        // rather than the request as received. Mirrors the original
        // logic from SmitedGrpcService.BuildTriggerRecord.
        var (sensationId, accepted, errorCode, errorField, zoneIds, intensity) = outcome switch
        {
            TriggerOutcome.Accepted a => (
                a.SensationId, true, (string?)null, (string?)null,
                (IReadOnlyList<string>)a.ResolvedZoneIds, a.ResolvedIntensityScale),
            TriggerOutcome.Rejected r => (
                string.Empty, false, r.Code.ToString(), r.Field,
                (IReadOnlyList<string>)input.ZoneIds, input.IntensityScale),
            _ => (
                string.Empty, false, "Unknown", (string?)null,
                (IReadOnlyList<string>)input.ZoneIds, input.IntensityScale),
        };
        return new TriggerRecord
        {
            Timestamp = _time.GetUtcNow(),
            BackendId = input.BackendId,
            SensationName = input.SensationName,
            SensationId = sensationId,
            ZoneIdsJson = JsonSerializer.Serialize(zoneIds),
            IntensityScale = intensity,
            Priority = input.Priority,
            ClientTraceId = input.ClientTraceId,
            Accepted = accepted,
            ErrorCode = errorCode,
            ErrorField = errorField,
        };
    }

    private static string SourceToString(TriggerSource source) => source switch
    {
        TriggerSource.Grpc => "grpc",
        TriggerSource.PanicHttp => "panic",
        TriggerSource.Admin => "admin",
        _ => "unknown",
    };
}

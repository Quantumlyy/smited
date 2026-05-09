namespace Smited.Daemon.History;

/// <summary>
/// Records daemon events to the history database. Every method is
/// best-effort — implementations swallow exceptions after logging so
/// that history failures never break the hot path. Callers should
/// fire-and-forget rather than awaiting history writes on the gRPC
/// response thread.
/// </summary>
internal interface IHistoryRecorder
{
    Task RecordTriggerAsync(TriggerRecord record, CancellationToken ct = default);

    Task RecordStopAsync(StopRecord record, CancellationToken ct = default);

    Task RecordPanicAsync(PanicRecord record, CancellationToken ct = default);

    Task RecordLifecycleAsync(LifecycleRecord record, CancellationToken ct = default);

    Task RecordBackendStateAsync(BackendStateRecord record, CancellationToken ct = default);
}

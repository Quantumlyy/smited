namespace Smited.Daemon.History;

/// <summary>
/// No-op <see cref="IHistoryRecorder"/> registered when
/// <c>Smited.History.Enabled</c> is <c>false</c>. Avoids null checks on
/// the hot path while letting users disable history entirely.
/// </summary>
internal sealed class NullHistoryRecorder : IHistoryRecorder
{
    public Task RecordTriggerAsync(TriggerRecord record, CancellationToken ct = default) => Task.CompletedTask;
    public Task RecordStopAsync(StopRecord record, CancellationToken ct = default) => Task.CompletedTask;
    public Task RecordPanicAsync(PanicRecord record, CancellationToken ct = default) => Task.CompletedTask;
    public Task RecordLifecycleAsync(LifecycleRecord record, CancellationToken ct = default) => Task.CompletedTask;
    public Task RecordBackendStateAsync(BackendStateRecord record, CancellationToken ct = default) => Task.CompletedTask;
}

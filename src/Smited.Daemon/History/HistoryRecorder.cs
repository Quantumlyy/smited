using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Smited.Daemon.History;

/// <summary>
/// Default <see cref="IHistoryRecorder"/> backed by a SQLite database via
/// EF Core. Each method opens a fresh <see cref="DbContext"/> from the
/// registered <see cref="IDbContextFactory{TContext}"/> so recorder calls
/// are thread-safe and short-lived.
/// </summary>
/// <remarks>
/// Failure semantics: every method catches all exceptions, logs at
/// <see cref="LogLevel.Warning"/>, and returns normally. The daemon's hot
/// path must not be coupled to history availability.
/// </remarks>
internal sealed class HistoryRecorder : IHistoryRecorder
{
    private readonly IDbContextFactory<HistoryDbContext> _factory;
    private readonly ILogger<HistoryRecorder> _log;

    public HistoryRecorder(IDbContextFactory<HistoryDbContext> factory, ILogger<HistoryRecorder> log)
    {
        _factory = factory;
        _log = log;
    }

    public Task RecordTriggerAsync(TriggerRecord record, CancellationToken ct = default) =>
        TryRecordAsync(record, (db, r) => db.Triggers.Add(r), "trigger", ct);

    public Task RecordStopAsync(StopRecord record, CancellationToken ct = default) =>
        TryRecordAsync(record, (db, r) => db.Stops.Add(r), "stop", ct);

    public Task RecordPanicAsync(PanicRecord record, CancellationToken ct = default) =>
        TryRecordAsync(record, (db, r) => db.Panics.Add(r), "panic", ct);

    public Task RecordLifecycleAsync(LifecycleRecord record, CancellationToken ct = default) =>
        TryRecordAsync(record, (db, r) => db.Lifecycle.Add(r), "lifecycle", ct);

    public Task RecordBackendStateAsync(BackendStateRecord record, CancellationToken ct = default) =>
        TryRecordAsync(record, (db, r) => db.BackendStates.Add(r), "backend-state", ct);

    private async Task TryRecordAsync<T>(
        T record,
        Action<HistoryDbContext, T> add,
        string kindForLog,
        CancellationToken ct)
        where T : class
    {
        try
        {
            await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            add(db, record);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // shutdown — fine
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to record {Kind} to history", kindForLog);
        }
    }
}

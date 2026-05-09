using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Smited.Daemon.Configuration;

namespace Smited.Daemon.History;

/// <summary>
/// Background service that prunes history rows older than
/// <see cref="SmitedOptions.HistoryOptions.RetentionDays"/> once per day,
/// then runs <c>VACUUM</c> weekly to reclaim space.
/// </summary>
/// <remarks>
/// Retention is a best-effort, low-priority background job. If a pass
/// fails it logs at warning level and waits for the next scheduled run.
/// Setting <c>RetentionDays</c> to <c>0</c> disables pruning — the
/// database grows unbounded, fine for low-volume personal use but worth
/// being aware of.
/// </remarks>
internal sealed class HistoryRetentionService : BackgroundService
{
    private static readonly TimeSpan PruneInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan VacuumInterval = TimeSpan.FromDays(7);

    private readonly IDbContextFactory<HistoryDbContext> _factory;
    private readonly TimeProvider _time;
    private readonly SmitedOptions _options;
    private readonly ILogger<HistoryRetentionService> _log;

    public HistoryRetentionService(
        IDbContextFactory<HistoryDbContext> factory,
        TimeProvider time,
        IOptions<SmitedOptions> options,
        ILogger<HistoryRetentionService> log)
    {
        _factory = factory;
        _time = time;
        _options = options.Value;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextVacuum = _time.GetUtcNow() + VacuumInterval;

        while (!stoppingToken.IsCancellationRequested)
        {
            await PruneAsync(stoppingToken).ConfigureAwait(false);

            if (_time.GetUtcNow() >= nextVacuum)
            {
                await VacuumAsync(stoppingToken).ConfigureAwait(false);
                nextVacuum = _time.GetUtcNow() + VacuumInterval;
            }

            try
            {
                await Task.Delay(PruneInterval, _time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
        }
    }

    private async Task PruneAsync(CancellationToken ct)
    {
        var days = _options.History.RetentionDays;
        if (days <= 0) return;

        var cutoff = _time.GetUtcNow().AddDays(-days);
        try
        {
            await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var triggers = await db.Triggers.Where(r => r.Timestamp < cutoff).ExecuteDeleteAsync(ct);
            var stops = await db.Stops.Where(r => r.Timestamp < cutoff).ExecuteDeleteAsync(ct);
            var panics = await db.Panics.Where(r => r.Timestamp < cutoff).ExecuteDeleteAsync(ct);
            var lifecycle = await db.Lifecycle.Where(r => r.Timestamp < cutoff).ExecuteDeleteAsync(ct);
            var backendStates = await db.BackendStates.Where(r => r.Timestamp < cutoff).ExecuteDeleteAsync(ct);

            _log.LogInformation(
                "History retention pass deleted {Triggers} triggers, {Stops} stops, {Panics} panics, {Lifecycle} lifecycle, {BackendStates} backend-state rows older than {Cutoff:o}",
                triggers, stops, panics, lifecycle, backendStates, cutoff);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "History retention pass failed; will retry next interval");
        }
    }

    private async Task VacuumAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            await db.Database.ExecuteSqlRawAsync("VACUUM;", ct).ConfigureAwait(false);
            _log.LogInformation("History database VACUUM completed");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "History VACUUM failed; will retry next interval");
        }
    }
}

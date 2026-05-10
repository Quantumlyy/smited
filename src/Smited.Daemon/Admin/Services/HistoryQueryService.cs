using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Smited.Daemon.History;

namespace Smited.Daemon.Admin.Services;

/// <summary>
/// Thin read-side wrapper around <see cref="HistoryDbContext"/>. The factory
/// is registered conditionally — when <c>Smited:History:Enabled = false</c>
/// it doesn't exist in DI at all — so this service resolves it via
/// <see cref="IServiceProvider"/>.<c>GetService</c> and returns empty
/// results when history is disabled.
/// </summary>
internal sealed class HistoryQueryService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<HistoryQueryService> _logger;

    public HistoryQueryService(IServiceProvider services, ILogger<HistoryQueryService> logger)
    {
        _services = services;
        _logger = logger;
    }

    public async Task<IReadOnlyList<TriggerRecord>> GetRecentAsync(
        int limit = 50, CancellationToken ct = default)
    {
        var factory = _services.GetService<IDbContextFactory<HistoryDbContext>>();
        if (factory is null) return Array.Empty<TriggerRecord>();

        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            // Order by Id rather than Timestamp because the SQLite EF
            // provider rejects DateTimeOffset in ORDER BY. Id is a
            // monotonically increasing primary key written at the same
            // moment as Timestamp, so the resulting row order is the
            // same.
            return await db.Triggers
                .OrderByDescending(t => t.Id)
                .Take(limit)
                .ToListAsync(ct);
        }
        catch (Exception ex)
        {
            // Init may not have completed (HistoryDbInitializer is a hosted
            // service that runs on startup). Treat as "no history yet."
            _logger.LogDebug(ex, "History query failed; returning empty list.");
            return Array.Empty<TriggerRecord>();
        }
    }
}

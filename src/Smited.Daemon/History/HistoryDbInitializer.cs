using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Smited.Daemon.History;

/// <summary>
/// <see cref="IHostedService"/> that runs <c>EnsureCreatedAsync</c> on the
/// history database at startup. Uses <c>EnsureCreated</c> rather than EF
/// migrations because the schema is not yet versioned; when the first
/// breaking schema change lands, switch to migrations and run them here
/// instead.
/// </summary>
internal sealed class HistoryDbInitializer : IHostedService
{
    private readonly IDbContextFactory<HistoryDbContext> _factory;
    private readonly string _dbPath;
    private readonly ILogger<HistoryDbInitializer> _log;

    public HistoryDbInitializer(
        IDbContextFactory<HistoryDbContext> factory,
        HistoryDbPath dbPath,
        ILogger<HistoryDbInitializer> log)
    {
        _factory = factory;
        _dbPath = dbPath.Value;
        _log = log;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await db.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
            _log.LogInformation("History database ready at {Path}", _dbPath);
        }
        catch (OperationCanceledException)
        {
            // shutdown — fine
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "History database initialization failed at {Path}; continuing without guaranteed history persistence",
                _dbPath);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Carries the resolved history database path through DI so banner and
/// initializer log messages can show the same path consistently.
/// </summary>
internal sealed record HistoryDbPath(string Value);

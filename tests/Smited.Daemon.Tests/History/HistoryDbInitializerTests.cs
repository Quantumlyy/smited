using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Smited.Daemon.History;
using Xunit;

namespace Smited.Daemon.Tests.History;

public class HistoryDbInitializerTests
{
    [Fact]
    public async Task StartAsync_swallows_database_failures()
    {
        var initializer = new HistoryDbInitializer(
            new ThrowingFactory(),
            new HistoryDbPath("/tmp/smited-history-unavailable.db"),
            NullLogger<HistoryDbInitializer>.Instance);

        var act = () => initializer.StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    private sealed class ThrowingFactory : IDbContextFactory<HistoryDbContext>
    {
        public HistoryDbContext CreateDbContext() => throw new InvalidOperationException("database unavailable");

        public Task<HistoryDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("database unavailable");
    }
}

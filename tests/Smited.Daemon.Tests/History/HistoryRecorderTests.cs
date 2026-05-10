using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Smited.Daemon.History;
using Xunit;

namespace Smited.Daemon.Tests.History;

public class HistoryRecorderTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DbContextOptions<HistoryDbContext> _opts;
    private readonly IDbContextFactory<HistoryDbContext> _factory;

    public HistoryRecorderTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "smited-history-" + Guid.NewGuid().ToString("N") + ".db");
        _opts = new DbContextOptionsBuilder<HistoryDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
        _factory = new SimpleDbContextFactory(_opts);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    [Fact]
    public async Task RecordTriggerAsync_writes_a_round_trippable_row()
    {
        var recorder = new HistoryRecorder(_factory, NullLogger<HistoryRecorder>.Instance);
        var record = new TriggerRecord
        {
            Timestamp = new DateTimeOffset(2026, 5, 9, 12, 0, 0, TimeSpan.Zero),
            BackendId = "mock-owo",
            SensationName = "compile_error_mild",
            SensationId = "id123",
            ZoneIdsJson = "[\"pectoral_l\"]",
            Priority = 0,
            ClientTraceId = "trace",
            Accepted = true,
        };

        await recorder.RecordTriggerAsync(record);

        await using var db = _factory.CreateDbContext();
        var row = db.Triggers.Single();
        row.BackendId.Should().Be("mock-owo");
        row.SensationName.Should().Be("compile_error_mild");
        row.Accepted.Should().BeTrue();
    }

    [Fact]
    public async Task RecordPanicAsync_persists_peer_and_count()
    {
        var recorder = new HistoryRecorder(_factory, NullLogger<HistoryRecorder>.Instance);

        await recorder.RecordPanicAsync(new PanicRecord
        {
            Timestamp = DateTimeOffset.UtcNow,
            Peer = "127.0.0.1",
            UserAgent = "curl/8.7",
            Ok = true,
            StoppedCount = 3,
        });

        await using var db = _factory.CreateDbContext();
        var row = db.Panics.Single();
        row.Peer.Should().Be("127.0.0.1");
        row.StoppedCount.Should().Be(3);
        row.Ok.Should().BeTrue();
    }

    [Fact]
    public async Task Recorder_swallows_database_failures()
    {
        // Use a factory that always throws.
        var recorder = new HistoryRecorder(new ThrowingFactory(), NullLogger<HistoryRecorder>.Instance);

        // Should not throw.
        await recorder.RecordTriggerAsync(new TriggerRecord());
    }

    [Fact]
    public async Task NullHistoryRecorder_is_a_no_op()
    {
        var recorder = new NullHistoryRecorder();

        await recorder.RecordTriggerAsync(new TriggerRecord());
        await recorder.RecordStopAsync(new StopRecord());
        await recorder.RecordPanicAsync(new PanicRecord());
        await recorder.RecordLifecycleAsync(new LifecycleRecord());
        await recorder.RecordBackendStateAsync(new BackendStateRecord());
    }

    private sealed class ThrowingFactory : IDbContextFactory<HistoryDbContext>
    {
        public HistoryDbContext CreateDbContext() => throw new InvalidOperationException("disk gremlin");

        public Task<HistoryDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("disk gremlin");
    }

    private sealed class SimpleDbContextFactory : IDbContextFactory<HistoryDbContext>
    {
        private readonly DbContextOptions<HistoryDbContext> _opts;

        public SimpleDbContextFactory(DbContextOptions<HistoryDbContext> opts)
        {
            _opts = opts;
        }

        public HistoryDbContext CreateDbContext() => new(_opts);

        public Task<HistoryDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
            Task.FromResult(new HistoryDbContext(_opts));
    }
}

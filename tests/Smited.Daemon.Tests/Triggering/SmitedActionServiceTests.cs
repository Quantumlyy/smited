using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Smited.Daemon.Backends.Internal;
using Smited.Daemon.Tests.Fixtures;
using Smited.Daemon.Triggering;
using Xunit;

namespace Smited.Daemon.Tests.Triggering;

/// <summary>
/// Round-N+1 fix #1 lock-in. The facade exists so admin-fired actions
/// land in the same history rows the gRPC and panic-HTTP paths write —
/// otherwise admin-only triggers/stops/panics are invisible to
/// postmortem queries against history. These tests assert that
/// dispatching through <see cref="SmitedActionService"/> with each
/// <see cref="TriggerSource"/> produces the expected
/// <c>StopRecord.Source</c> string and a paired <c>PanicRecord</c>.
/// </summary>
public class SmitedActionServiceTests : IDisposable
{
    private readonly DaemonFixture _fixture;

    public SmitedActionServiceTests()
    {
        _fixture = new DaemonFixture(seed: root =>
            SampleSensations.WriteOwo(root, "compile_error_mild.json", SampleSensations.CompileErrorMild));
    }

    public void Dispose() => _fixture.Dispose();

    private SmitedActionService Actions =>
        _fixture.Services.GetRequiredService<SmitedActionService>();

    [Fact]
    public async Task Admin_trigger_records_a_TriggerRecord()
    {
        var input = new ResolvedTriggerInput(
            BackendId: "mock-owo",
            SensationName: "compile_error_mild",
            InlineMicrosensations: null,
            ZoneIds: Array.Empty<string>(),
            IntensityScale: null,
            Priority: 0,
            ClientTraceId: "trace-admin-trigger");

        var outcome = await Actions.TriggerAsync(input, TriggerSource.Admin, default);
        outcome.Should().BeOfType<TriggerOutcome.Accepted>();

        await EventuallyAsync(async () =>
        {
            await using var db = await _fixture.HistoryFactory.CreateDbContextAsync();
            var row = await db.Triggers
                .Where(r => r.ClientTraceId == "trace-admin-trigger")
                .FirstOrDefaultAsync();
            row.Should().NotBeNull();
            row!.Accepted.Should().BeTrue();
            row.BackendId.Should().Be("mock-owo");
        });
    }

    [Fact]
    public Task StopAsync_grpc_writes_StopRecord_with_grpc_source() =>
        AssertStopAsyncSource(TriggerSource.Grpc, "grpc");

    [Fact]
    public Task StopAsync_panic_http_writes_StopRecord_with_panic_source() =>
        AssertStopAsyncSource(TriggerSource.PanicHttp, "panic");

    [Fact]
    public Task StopAsync_admin_writes_StopRecord_with_admin_source() =>
        AssertStopAsyncSource(TriggerSource.Admin, "admin");

    private async Task AssertStopAsyncSource(TriggerSource source, string expectedSource)
    {
        var stopped = await Actions.StopAsync(
            new BackendStopRequest(SensationId: null, All: true), source, default);
        stopped.Should().Be(0); // nothing playing

        await EventuallyAsync(async () =>
        {
            await using var db = await _fixture.HistoryFactory.CreateDbContextAsync();
            var matching = await db.Stops
                .Where(s => s.Source == expectedSource && s.All)
                .OrderByDescending(s => s.Id)
                .FirstOrDefaultAsync();
            matching.Should().NotBeNull();
            matching!.Source.Should().Be(expectedSource);
        });
    }

    /// <summary>
    /// Round-N+3 fix #1 regression test. <c>StopBackendAsync</c>
    /// produces a StopRecord with <c>All=false</c> and <c>BackendId</c>
    /// populated — the historical gRPC <c>Stop{backend_id}</c> shape.
    /// Pre-fix the facade copy-pasted <c>All=true</c> from the panic
    /// path, collapsing backend-scoped stops into daemon-wide ones in
    /// the database; postmortems against <c>WHERE All=false</c> would
    /// have missed every admin-triggered backend stop.
    /// </summary>
    [Fact]
    public async Task StopBackendAsync_records_All_false_with_BackendId_set()
    {
        await Actions.StopBackendAsync("mock-owo", TriggerSource.Admin, default);

        await EventuallyAsync(async () =>
        {
            await using var db = await _fixture.HistoryFactory.CreateDbContextAsync();
            var row = await db.Stops
                .Where(s => s.BackendId == "mock-owo" && s.Source == "admin")
                .OrderByDescending(s => s.Id)
                .FirstOrDefaultAsync();
            row.Should().NotBeNull();
            row!.All.Should().BeFalse(
                "backend-scoped stop is not daemon-wide; the All flag is reserved for that case");
            row.BackendId.Should().Be("mock-owo");
            row.SensationId.Should().BeNull();
        });
    }

    /// <summary>
    /// Locks in the panic-vs-backend-stop distinction in the recording
    /// layer alongside the fix above. A future refactor that
    /// re-collapses the two would have to flip this expectation,
    /// which forces a deliberate choice rather than a copy-paste
    /// regression.
    /// </summary>
    [Fact]
    public async Task PanicAsync_records_All_true_with_no_BackendId()
    {
        await Actions.PanicAsync(TriggerSource.Admin, peer: null, userAgent: null, default);

        await EventuallyAsync(async () =>
        {
            await using var db = await _fixture.HistoryFactory.CreateDbContextAsync();
            var row = await db.Stops
                .Where(s => s.Source == "admin" && s.All)
                .OrderByDescending(s => s.Id)
                .FirstOrDefaultAsync();
            row.Should().NotBeNull();
            row!.All.Should().BeTrue("panic stops are daemon-wide");
            row.BackendId.Should().BeNull("panic isn't scoped to a single backend");
        });
    }

    [Fact]
    public async Task Admin_panic_writes_panic_and_stop_rows()
    {
        await Actions.PanicAsync(TriggerSource.Admin, peer: null, userAgent: null, default);

        await EventuallyAsync(async () =>
        {
            await using var db = await _fixture.HistoryFactory.CreateDbContextAsync();
            (await db.Panics.AnyAsync()).Should().BeTrue();
            (await db.Stops.Where(s => s.Source == "admin" && s.All).AnyAsync()).Should().BeTrue();
        });
    }

    [Fact]
    public async Task Panic_HTTP_path_still_records_with_panic_source()
    {
        // Hit the HTTP endpoint to ensure the refactored PanicEndpoint
        // still produces panic-source rows (the existing HistoryFlowTests
        // covers this too; this duplicate keeps the contract local to the
        // facade's regression battery).
        var response = await _fixture.PanicHttpClient.PostAsync("/panic", content: null);
        response.EnsureSuccessStatusCode();

        await EventuallyAsync(async () =>
        {
            await using var db = await _fixture.HistoryFactory.CreateDbContextAsync();
            (await db.Stops.Where(s => s.Source == "panic" && s.All).AnyAsync()).Should().BeTrue();
        });
    }

    private static async Task EventuallyAsync(Func<Task> assertion)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await assertion();
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                await Task.Delay(50);
            }
        }
        if (last is not null) throw last;
    }
}

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Smited.Daemon.History;
using Smited.Daemon.Tests.Fixtures;
using Smited.V1;
using Xunit;

namespace Smited.Daemon.Tests.EndToEnd;

public class HistoryFlowTests : IDisposable
{
    private readonly DaemonFixture _fixture;

    public HistoryFlowTests()
    {
        _fixture = new DaemonFixture(seed: root =>
            SampleSensations.WriteOwo(root, "compile_error_mild.json", SampleSensations.CompileErrorMild));
    }

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task Trigger_writes_a_TriggerRecord_row()
    {
        await _fixture.Client.TriggerAsync(new TriggerRequest
        {
            BackendId = "mock-owo",
            SensationName = "compile_error_mild",
            ClientTraceId = "trace-history",
        });

        await EventuallyAsync(async () =>
        {
            await using var db = await _fixture.HistoryFactory.CreateDbContextAsync();
            var row = await db.Triggers.OrderByDescending(r => r.Id).FirstOrDefaultAsync();
            row.Should().NotBeNull();
            row!.BackendId.Should().Be("mock-owo");
            row.SensationName.Should().Be("compile_error_mild");
            row.Accepted.Should().BeTrue();
            row.ClientTraceId.Should().Be("trace-history");
        });
    }

    [Fact]
    public async Task Trigger_with_unknown_sensation_writes_a_rejected_TriggerRecord()
    {
        await _fixture.Client.TriggerAsync(new TriggerRequest
        {
            BackendId = "mock-owo",
            SensationName = "no_such_thing",
            ClientTraceId = "trace-bad",
        });

        await EventuallyAsync(async () =>
        {
            await using var db = await _fixture.HistoryFactory.CreateDbContextAsync();
            var row = await db.Triggers.Where(r => r.ClientTraceId == "trace-bad").FirstOrDefaultAsync();
            row.Should().NotBeNull();
            row!.Accepted.Should().BeFalse();
            row.ErrorCode.Should().Contain("SensationNotFound");
        });
    }

    [Fact]
    public async Task Panic_writes_panic_and_stop_rows()
    {
        var response = await _fixture.PanicHttpClient.PostAsync("/panic", content: null);
        response.EnsureSuccessStatusCode();

        await EventuallyAsync(async () =>
        {
            await using var db = await _fixture.HistoryFactory.CreateDbContextAsync();
            (await db.Panics.AnyAsync()).Should().BeTrue();
            (await db.Stops.Where(s => s.Source == "panic").AnyAsync()).Should().BeTrue();
        });
    }

    [Fact]
    public async Task Backend_register_during_boot_writes_a_BackendStateRecord()
    {
        // The fixture has already booted; the registration row should exist.
        await EventuallyAsync(async () =>
        {
            await using var db = await _fixture.HistoryFactory.CreateDbContextAsync();
            var row = await db.BackendStates
                .Where(r => r.BackendId == "mock-owo" && r.Event == "registered")
                .FirstOrDefaultAsync();
            row.Should().NotBeNull();
            row!.Kind.Should().Be("owo_skin");
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

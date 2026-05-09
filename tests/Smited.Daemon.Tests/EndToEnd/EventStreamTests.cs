using FluentAssertions;
using Grpc.Core;
using Smited.Daemon.Tests.Fixtures;
using Smited.V1;
using Xunit;

namespace Smited.Daemon.Tests.EndToEnd;

public class EventStreamTests : IDisposable
{
    private readonly DaemonFixture _fixture;

    public EventStreamTests()
    {
        _fixture = new DaemonFixture(seed: root =>
            SampleSensations.WriteOwo(root, "compile_error_mild.json", SampleSensations.CompileErrorMild));
    }

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task Subscriber_receives_Started_then_Completed_for_a_natural_sensation()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        using var stream = _fixture.Client.SubscribeEvents(
            new SubscribeEventsRequest(), cancellationToken: cts.Token);

        // Give the subscriber a beat to attach.
        await Task.Delay(100, cts.Token);

        var collect = CollectAsync(stream.ResponseStream, count: 2, cts.Token);

        await _fixture.Client.TriggerAsync(new TriggerRequest
        {
            BackendId = "mock-owo",
            SensationName = "compile_error_mild",
            ClientTraceId = "trace",
        });

        // Sample says estimated_duration 0.4s — advance well past that.
        _fixture.Time.Advance(TimeSpan.FromSeconds(1));

        var events = await collect;

        events.Should().HaveCount(2);
        events[0].Kind.Should().Be(EventKind.SensationStarted);
        events[1].Kind.Should().Be(EventKind.SensationCompleted);
        events[0].BackendId.Should().Be("mock-owo");
    }

    [Fact]
    public async Task Filter_by_kind_excludes_other_kinds()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var request = new SubscribeEventsRequest();
        request.Kinds.Add(EventKind.SensationStarted);

        using var stream = _fixture.Client.SubscribeEvents(request, cancellationToken: cts.Token);

        // Eagerly start collection so the gRPC handler attaches its
        // subscription before the trigger fires.
        var collect = CollectAsync(stream.ResponseStream, count: 1, cts.Token);
        await Task.Delay(250, cts.Token);

        await _fixture.Client.TriggerAsync(new TriggerRequest
        {
            BackendId = "mock-owo",
            SensationName = "compile_error_mild",
            ClientTraceId = "trace",
        });
        _fixture.Time.Advance(TimeSpan.FromSeconds(1));

        var events = await collect;
        events.Should().HaveCount(1);
        events[0].Kind.Should().Be(EventKind.SensationStarted);
    }

    [Fact]
    public async Task Filter_by_backend_id_excludes_other_backends()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        var request = new SubscribeEventsRequest();
        request.BackendIds.Add("never-existed");

        using var stream = _fixture.Client.SubscribeEvents(request, cancellationToken: cts.Token);
        await Task.Delay(100, cts.Token);

        await _fixture.Client.TriggerAsync(new TriggerRequest
        {
            BackendId = "mock-owo",
            SensationName = "compile_error_mild",
            ClientTraceId = "trace",
        });
        _fixture.Time.Advance(TimeSpan.FromSeconds(1));

        // Wait briefly to confirm no events arrive for the unrelated backend filter.
        var collect = CollectAsync(stream.ResponseStream, count: 1, cts.Token);
        var winner = await Task.WhenAny(collect, Task.Delay(500));
        winner.Should().NotBe((Task)collect);
    }

    private static async Task<IReadOnlyList<Event>> CollectAsync(
        IAsyncStreamReader<Event> reader, int count, CancellationToken ct)
    {
        var result = new List<Event>(count);
        try
        {
            while (result.Count < count && await reader.MoveNext(ct))
            {
                result.Add(reader.Current);
            }
        }
        catch (OperationCanceledException) { }
        catch (Grpc.Core.RpcException) { }
        return result;
    }
}

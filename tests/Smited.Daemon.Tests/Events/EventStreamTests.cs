using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Smited.Daemon.Backends.Internal;
using Smited.Daemon.Configuration;
using Smited.Daemon.Events;
using Smited.V1;
using Xunit;

namespace Smited.Daemon.Tests.Events;

public class EventStreamTests
{
    private static (EventBus Bus, EventStream Stream) NewStream(
        int bufferCapacity = 64,
        string slowPolicy = "drop_oldest")
    {
        var bus = new EventBus(NullLogger<EventBus>.Instance);
        var opts = Options.Create(new SmitedOptions
        {
            EventBus = new SmitedOptions.EventBusOptions
            {
                BufferCapacity = bufferCapacity,
                SlowSubscriberPolicy = slowPolicy,
            },
        });
        var stream = new EventStream(bus, opts, NullLogger<EventStream>.Instance);
        return (bus, stream);
    }

    private static SensationStarted Started(string backendId, string id) =>
        new(backendId, DateTimeOffset.UtcNow, id, SensationName: null, ClientTraceId: "",
            ZoneIds: Array.Empty<string>(), IntensityPercent: null);

    private static SensationCompleted Completed(string backendId, string id) =>
        new(backendId, DateTimeOffset.UtcNow, id, SensationName: null, ClientTraceId: "");

    [Fact]
    public async Task Filter_by_kind_drops_other_kinds()
    {
        var (bus, stream) = NewStream();
        var filters = new SubscribeFilters(
            new HashSet<EventKind> { EventKind.SensationStarted },
            new HashSet<string>());
        using var cts = new CancellationTokenSource();

        var task = CollectAsync(stream.StreamAsync(filters, "test", cts.Token), expected: 1);

        bus.Publish(Started("mock", "a"));
        bus.Publish(Completed("mock", "a"));
        bus.Publish(Started("mock", "b"));
        bus.Publish(Completed("mock", "b"));

        var got = await task.WaitAsync(TimeSpan.FromSeconds(2));

        got.Should().HaveCount(1);
        got[0].Should().BeOfType<SensationStarted>()
            .Which.SensationId.Should().Be("a");

        await cts.CancelAsync();
    }

    [Fact]
    public async Task Filter_by_backend_id_drops_other_backends()
    {
        var (bus, stream) = NewStream();
        var filters = new SubscribeFilters(
            new HashSet<EventKind>(),
            new HashSet<string> { "mock" });
        using var cts = new CancellationTokenSource();

        var task = CollectAsync(stream.StreamAsync(filters, "test", cts.Token), expected: 2);

        bus.Publish(Started("other", "x"));
        bus.Publish(Started("mock", "a"));
        bus.Publish(Completed("other", "x"));
        bus.Publish(Completed("mock", "a"));

        var got = await task.WaitAsync(TimeSpan.FromSeconds(2));

        got.Select(e => e.BackendId).Should().AllBe("mock");

        await cts.CancelAsync();
    }

    [Fact]
    public async Task Cancellation_completes_the_enumeration_cleanly()
    {
        var (_, stream) = NewStream();
        var filters = SubscribeFilters.None;
        using var cts = new CancellationTokenSource();

        var task = CollectAsync(stream.StreamAsync(filters, "test", cts.Token), expected: 0);

        await cts.CancelAsync();

        await task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static async Task<List<BackendEvent>> CollectAsync(
        IAsyncEnumerable<BackendEvent> source,
        int expected)
    {
        var result = new List<BackendEvent>(expected);
        try
        {
            await foreach (var evt in source)
            {
                result.Add(evt);
                if (result.Count >= expected)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // expected on cancellation
        }
        return result;
    }
}

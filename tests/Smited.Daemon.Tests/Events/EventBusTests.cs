using System.Threading.Channels;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Smited.Daemon.Backends.Internal;
using Smited.Daemon.Events;
using Xunit;

namespace Smited.Daemon.Tests.Events;

public class EventBusTests
{
    private static EventBus NewBus() => new(NullLogger<EventBus>.Instance);

    private static SensationStarted Started(string id) =>
        new("mock", DateTimeOffset.UtcNow, id, SensationName: null, ClientTraceId: "",
            ZoneIds: Array.Empty<string>(), IntensityPercent: null);

    [Fact]
    public async Task Two_subscribers_receive_the_same_published_event()
    {
        var bus = NewBus();
        await using var a = bus.Subscribe(16, BoundedChannelFullMode.DropOldest);
        await using var b = bus.Subscribe(16, BoundedChannelFullMode.DropOldest);

        bus.Publish(Started("e1"));
        bus.Publish(Started("e2"));

        var got1 = await ReadCount(a.Reader, 2);
        var got2 = await ReadCount(b.Reader, 2);

        got1.Select(e => ((SensationStarted)e).SensationId).Should().Equal("e1", "e2");
        got2.Select(e => ((SensationStarted)e).SensationId).Should().Equal("e1", "e2");
    }

    [Fact]
    public async Task Slow_subscriber_with_drop_oldest_does_not_block_fast_subscriber()
    {
        var bus = NewBus();
        await using var fast = bus.Subscribe(64, BoundedChannelFullMode.DropOldest);
        await using var slow = bus.Subscribe(2, BoundedChannelFullMode.DropOldest);

        for (int i = 0; i < 10; i++)
        {
            bus.Publish(Started($"e{i}"));
        }

        var fastEvents = await ReadCount(fast.Reader, 10);
        fastEvents.Should().HaveCount(10);

        // Slow subscriber kept only the last `capacity` events.
        var slowEvents = await ReadAll(slow.Reader, expectedMax: 2);
        slowEvents.Should().HaveCount(2);
        slowEvents.Select(e => ((SensationStarted)e).SensationId)
            .Should().Equal("e8", "e9");
    }

    [Fact]
    public async Task Disposing_subscription_completes_the_reader_and_stops_delivery()
    {
        var bus = NewBus();
        var sub = bus.Subscribe(16, BoundedChannelFullMode.DropOldest);

        bus.Publish(Started("e1"));
        var first = await sub.Reader.ReadAsync();
        ((SensationStarted)first).SensationId.Should().Be("e1");

        await sub.DisposeAsync();
        bus.Publish(Started("e2"));

        // Reader is completed; reading drains nothing more.
        var more = sub.Reader.TryRead(out _);
        more.Should().BeFalse();
        bus.SubscriberCount.Should().Be(0);
    }

    private static async Task<IReadOnlyList<BackendEvent>> ReadCount(
        ChannelReader<BackendEvent> reader, int count)
    {
        var result = new List<BackendEvent>(count);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        for (int i = 0; i < count; i++)
        {
            result.Add(await reader.ReadAsync(cts.Token));
        }
        return result;
    }

    private static async Task<IReadOnlyList<BackendEvent>> ReadAll(
        ChannelReader<BackendEvent> reader, int expectedMax)
    {
        // Give the bus a beat to flush, then drain whatever is buffered.
        await Task.Delay(10);
        var result = new List<BackendEvent>(expectedMax);
        while (reader.TryRead(out var evt))
        {
            result.Add(evt);
        }
        return result;
    }
}

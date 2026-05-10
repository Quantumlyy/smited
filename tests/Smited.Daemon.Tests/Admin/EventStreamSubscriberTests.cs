using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Smited.Daemon.Admin.Services;
using Smited.Daemon.Backends.Internal;
using Smited.Daemon.Events;
using Smited.Daemon.Tests.Fixtures;
using Smited.V1;
using Xunit;

namespace Smited.Daemon.Tests.Admin;

public class EventStreamSubscriberTests : IDisposable
{
    private readonly DaemonFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    /// <summary>
    /// Regression: when <c>EventStreamSubscriber</c> was registered as
    /// <c>Scoped</c>, every Blazor component on a circuit shared one
    /// underlying <see cref="EventBus.Subscription"/> — and because
    /// <c>ChannelReader&lt;T&gt;</c> is single-consumer, each event was
    /// delivered to whichever component won the read race. Transient
    /// registration gives each component its own subscription, so all
    /// components see all events.
    /// </summary>
    [Fact]
    public async Task Two_subscribers_each_receive_every_event()
    {
        var bus = _fixture.EventBus;
        var sub1 = _fixture.Services.GetRequiredService<EventStreamSubscriber>();
        var sub2 = _fixture.Services.GetRequiredService<EventStreamSubscriber>();
        sub1.Should().NotBeSameAs(sub2, "transient registration must yield a fresh instance per resolution");

        var received1 = new List<BackendEvent>();
        var received2 = new List<BackendEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        // Capture the baseline subscriber count BEFORE starting the
        // background loops so the wait helper doesn't race with the
        // attachments themselves.
        var baseline = bus.SubscriberCount;

        var loop1 = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in sub1.StreamAsync(ct: cts.Token))
                {
                    received1.Add(evt);
                }
            }
            catch (OperationCanceledException) { }
        });
        var loop2 = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in sub2.StreamAsync(ct: cts.Token))
                {
                    received2.Add(evt);
                }
            }
            catch (OperationCanceledException) { }
        });

        await WaitForSubscribersAsync(bus, expected: baseline + 2, timeout: TimeSpan.FromSeconds(1));

        var snap = new BackendSummarySnapshot("evt-stream-test", "owo_skin", "Test", BackendStatus.Ready, Array.Empty<string>());
        for (var i = 0; i < 3; i++)
        {
            bus.Publish(new BackendLifecycleEvent(
                "evt-stream-test",
                DateTimeOffset.UtcNow,
                BackendLifecycleChange.StatusChanged,
                snap,
                Reason: $"event-{i}"));
        }

        // Drain time: small enough to keep the test fast, large enough to
        // let the channel readers see all three events.
        await Task.Delay(150);
        cts.Cancel();
        await loop1;
        await loop2;

        // Filter on the synthetic BackendId so unrelated boot-time
        // lifecycle events (e.g. mock-owo registration) don't pollute
        // the count.
        received1.OfType<BackendLifecycleEvent>()
            .Where(e => e.BackendId == "evt-stream-test")
            .Should().HaveCount(3);
        received2.OfType<BackendLifecycleEvent>()
            .Where(e => e.BackendId == "evt-stream-test")
            .Should().HaveCount(3);

        await sub1.DisposeAsync();
        await sub2.DisposeAsync();
    }

    /// <summary>
    /// Locks in the no-leak contract from Round-N+1 fix #4: components
    /// dispose their <see cref="EventStreamSubscriber"/> on unmount, and
    /// disposing the subscriber must in turn release the underlying
    /// <see cref="EventBus.Subscription"/>. Pre-fix, the subscription
    /// leaked until the Blazor circuit ended (browser close); the
    /// regression would re-introduce that leak silently.
    /// </summary>
    [Fact]
    public async Task Disposing_subscriber_releases_underlying_subscription()
    {
        var bus = _fixture.EventBus;
        var sub = _fixture.Services.GetRequiredService<EventStreamSubscriber>();
        var baseline = bus.SubscriberCount;

        using var cts = new CancellationTokenSource();
        var stream = sub.StreamAsync(ct: cts.Token);
        var enumerator = stream.GetAsyncEnumerator();
        var moveNext = enumerator.MoveNextAsync().AsTask();

        // Wait for the subscription to actually attach to the bus.
        await WaitForSubscribersAsync(bus, expected: baseline + 1, timeout: TimeSpan.FromSeconds(1));
        bus.SubscriberCount.Should().Be(baseline + 1);

        await sub.DisposeAsync();
        cts.Cancel();
        try { await moveNext; } catch { }

        bus.SubscriberCount.Should().Be(baseline,
            "disposing the subscriber must release its underlying EventBus.Subscription");

        await enumerator.DisposeAsync();
    }

    private static async Task WaitForSubscribersAsync(EventBus bus, int expected, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (bus.SubscriberCount < expected && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
    }
}

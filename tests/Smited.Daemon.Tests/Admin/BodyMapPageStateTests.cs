using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Smited.Daemon.Admin.Services;
using Smited.Daemon.Backends.Internal;
using Smited.Daemon.Events;
using Xunit;

namespace Smited.Daemon.Tests.Admin;

public class BodyMapPageStateTests
{
    private static (BodyMapPageState state, EventBus bus, FakeTimeProvider time) NewState()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 5, 11, 12, 0, 0, TimeSpan.Zero));
        var bus = new EventBus(NullLogger<EventBus>.Instance);
        var state = new BodyMapPageState(bus, time, NullLogger<BodyMapPageState>.Instance);
        return (state, bus, time);
    }

    /// <summary>
    /// Publishes <paramref name="evt"/> and waits up to <paramref name="timeout"/>
    /// for the next <see cref="BodyMapPageState.StateChanged"/> emission.
    /// The consumer runs on a background task so without this latch the
    /// snapshot read can race the publish.
    /// </summary>
    private static async Task PublishAndAwait(
        BodyMapPageState state,
        EventBus bus,
        BackendEvent evt,
        TimeSpan? timeout = null)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler() => tcs.TrySetResult();
        state.StateChanged += Handler;
        try
        {
            bus.Publish(evt);
            await tcs.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(2));
        }
        finally
        {
            state.StateChanged -= Handler;
        }
    }

    [Fact]
    public async Task SensationStarted_marks_every_zone_active()
    {
        var (state, bus, time) = NewState();
        await state.StartAsync(default);
        await using var __ = new AsyncDisposable(() => state.StopAsync(default));

        var evt = new SensationStarted(
            BackendId: "mock-owo",
            Timestamp: time.GetUtcNow(),
            SensationId: "s1",
            SensationName: "test",
            ClientTraceId: "trace",
            ZoneIds: new[] { "pectoral_l", "pectoral_r" },
            IntensityPercent: 70u);

        await PublishAndAwait(state, bus, evt);

        var snapshot = state.Snapshot();
        snapshot[("mock-owo", "pectoral_l")].IsActive.Should().BeTrue();
        snapshot[("mock-owo", "pectoral_r")].IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task SensationStarted_records_LastFiredAt_and_LastIntensity()
    {
        var (state, bus, time) = NewState();
        await state.StartAsync(default);
        await using var __ = new AsyncDisposable(() => state.StopAsync(default));

        var firedAt = time.GetUtcNow();

        await PublishAndAwait(state, bus, new SensationStarted(
            "mock-owo", firedAt, "s1", "test", "trace",
            ZoneIds: new[] { "pectoral_l" }, IntensityPercent: 80u));

        var activity = state.Snapshot()[("mock-owo", "pectoral_l")];
        activity.LastFiredAt.Should().Be(firedAt);
        activity.LastIntensity.Should().Be(80u);
    }

    [Fact]
    public async Task SensationStarted_with_null_intensity_records_default_50()
    {
        var (state, bus, time) = NewState();
        await state.StartAsync(default);
        await using var __ = new AsyncDisposable(() => state.StopAsync(default));

        await PublishAndAwait(state, bus, new SensationStarted(
            "mock-owo", time.GetUtcNow(), "s1", "test", "trace",
            ZoneIds: new[] { "pectoral_l" }, IntensityPercent: null));

        var activity = state.Snapshot()[("mock-owo", "pectoral_l")];
        activity.LastIntensity.Should().Be(50u, "null intensity falls back to a midpoint default for rendering");
    }

    [Fact]
    public async Task SensationCompleted_clears_active_but_preserves_LastFiredAt()
    {
        var (state, bus, time) = NewState();
        await state.StartAsync(default);
        await using var __ = new AsyncDisposable(() => state.StopAsync(default));

        var firedAt = time.GetUtcNow();
        await PublishAndAwait(state, bus, new SensationStarted(
            "mock-owo", firedAt, "s1", "test", "trace",
            ZoneIds: new[] { "pectoral_l" }, IntensityPercent: 60u));

        time.Advance(TimeSpan.FromMilliseconds(500));

        await PublishAndAwait(state, bus, new SensationCompleted(
            "mock-owo", time.GetUtcNow(), "s1", "test", "trace"));

        var activity = state.Snapshot()[("mock-owo", "pectoral_l")];
        activity.IsActive.Should().BeFalse("the sensation has ended");
        activity.LastFiredAt.Should().Be(firedAt, "LastFiredAt must be preserved to drive the fade");
    }

    [Fact]
    public async Task SensationCancelled_clears_active_zones_for_the_backend()
    {
        var (state, bus, time) = NewState();
        await state.StartAsync(default);
        await using var __ = new AsyncDisposable(() => state.StopAsync(default));

        await PublishAndAwait(state, bus, new SensationStarted(
            "mock-owo", time.GetUtcNow(), "s1", "test", "trace",
            ZoneIds: new[] { "pectoral_l", "arm_l" }, IntensityPercent: 60u));

        await PublishAndAwait(state, bus, new SensationCancelled(
            "mock-owo", time.GetUtcNow(), "s1", "test", "trace", Reason: "panic"));

        var snapshot = state.Snapshot();
        snapshot[("mock-owo", "pectoral_l")].IsActive.Should().BeFalse();
        snapshot[("mock-owo", "arm_l")].IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task SensationCompleted_only_clears_its_own_backend()
    {
        var (state, bus, time) = NewState();
        await state.StartAsync(default);
        await using var __ = new AsyncDisposable(() => state.StopAsync(default));

        await PublishAndAwait(state, bus, new SensationStarted(
            "mock-owo", time.GetUtcNow(), "s1", "test", "trace",
            ZoneIds: new[] { "pectoral_l" }, IntensityPercent: 60u));
        await PublishAndAwait(state, bus, new SensationStarted(
            "mock-bhaptics", time.GetUtcNow(), "s2", "test", "trace2",
            ZoneIds: new[] { "vest_back" }, IntensityPercent: 60u));

        await PublishAndAwait(state, bus, new SensationCompleted(
            "mock-owo", time.GetUtcNow(), "s1", "test", "trace"));

        var snapshot = state.Snapshot();
        snapshot[("mock-owo", "pectoral_l")].IsActive.Should().BeFalse();
        snapshot[("mock-bhaptics", "vest_back")].IsActive.Should()
            .BeTrue("Completed on one backend must not touch other backends' zones");
    }

    [Fact]
    public async Task StateChanged_fires_on_relevant_events()
    {
        var (state, bus, time) = NewState();
        await state.StartAsync(default);
        await using var __ = new AsyncDisposable(() => state.StopAsync(default));

        await PublishAndAwait(state, bus, new SensationStarted(
            "mock-owo", time.GetUtcNow(), "s1", "test", "trace",
            ZoneIds: new[] { "pectoral_l" }, IntensityPercent: 60u));

        await PublishAndAwait(state, bus, new SensationCompleted(
            "mock-owo", time.GetUtcNow(), "s1", "test", "trace"));

        // Both calls above completed without timeout — proves StateChanged
        // fired for both event kinds. The PublishAndAwait helper would
        // throw on timeout if StateChanged hadn't fired.
    }

    [Fact]
    public async Task SensationStarted_with_empty_zone_list_does_not_fire_StateChanged()
    {
        var (state, bus, time) = NewState();
        await state.StartAsync(default);
        await using var __ = new AsyncDisposable(() => state.StopAsync(default));

        var fired = 0;
        state.StateChanged += () => Interlocked.Increment(ref fired);

        bus.Publish(new SensationStarted(
            "mock-owo", time.GetUtcNow(), "s1", "test", "trace",
            ZoneIds: Array.Empty<string>(), IntensityPercent: 60u));

        // Empty-zone Started can't change any zone activity; let the
        // consumer drain and confirm we didn't flap StateChanged anyway.
        await Task.Delay(50);
        fired.Should().Be(0);
    }

    /// <summary>
    /// Small helper so tests can <c>await using</c> a delegate-driven
    /// disposal without inventing a per-test record type.
    /// </summary>
    private sealed class AsyncDisposable : IAsyncDisposable
    {
        private readonly Func<Task> _stop;
        public AsyncDisposable(Func<Task> stop) => _stop = stop;
        public ValueTask DisposeAsync() => new(_stop());
    }
}

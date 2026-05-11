using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Smited.Daemon.Admin.Services;
using Smited.Daemon.Backends;
using Smited.Daemon.Backends.Internal;
using Smited.Daemon.Events;
using Smited.Daemon.Tests.Fixtures;
using Smited.V1;
using Xunit;

namespace Smited.Daemon.Tests.Admin;

public class BodyMapPageStateTests
{
    private static (BodyMapPageState state, EventBus bus, FakeTimeProvider time, BackendRegistry registry) NewState()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 5, 11, 12, 0, 0, TimeSpan.Zero));
        var bus = new EventBus(NullLogger<EventBus>.Instance);
        var registry = new BackendRegistry(bus, time);
        var state = new BodyMapPageState(bus, time, registry, NullLogger<BodyMapPageState>.Instance);
        return (state, bus, time, registry);
    }

    private static FakeBackend NewFakeBackendWithGroup(string id, string groupId, params string[] members)
    {
        var topo = new ZoneTopology();
        foreach (var m in members)
        {
            topo.Zones.Add(new Zone
            {
                Id = m,
                DisplayName = m,
                Position = new PositionHint { X = 0.5f, Y = 0.5f, Z = 0.5f, Frame = "body" },
            });
        }
        var group = new ZoneGroup { Id = groupId, DisplayName = groupId };
        foreach (var m in members) group.ZoneIds.Add(m);
        topo.Groups.Add(group);
        return new FakeBackend(id: id) { Zones = topo };
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
        var (state, bus, time, _) = NewState();
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
        var (state, bus, time, _) = NewState();
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
        var (state, bus, time, _) = NewState();
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
        var (state, bus, time, _) = NewState();
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
        var (state, bus, time, _) = NewState();
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
        var (state, bus, time, _) = NewState();
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
        var (state, bus, time, _) = NewState();
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
        var (state, bus, time, _) = NewState();
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
    /// Group-triggered sensations (e.g. <c>"torso"</c>, <c>"arms"</c>,
    /// <c>"all"</c> on OWO) must expand to their leaf zones in the
    /// activity map so the renderer — which iterates
    /// <c>backend.Zones.Zones</c> — actually sees the activity. Without
    /// expansion, firing the OWO bundled <c>deploy_success</c> sample
    /// (which targets the <c>torso</c> group by default) would store
    /// activity under <c>(backend, "torso")</c> and the renderer would
    /// see nothing.
    /// </summary>
    [Fact]
    public async Task SensationStarted_with_group_id_expands_to_leaf_zones()
    {
        var (state, bus, time, registry) = NewState();
        var backend = NewFakeBackendWithGroup(
            id: "fake-owo",
            groupId: "torso",
            members: new[] { "pectoral_l", "pectoral_r", "abdominal_l", "abdominal_r" });
        registry.Register(backend);

        await state.StartAsync(default);
        await using var __ = new AsyncDisposable(() => state.StopAsync(default));

        await PublishAndAwait(state, bus, new SensationStarted(
            "fake-owo", time.GetUtcNow(), "s1", "deploy_success", "trace",
            ZoneIds: new[] { "torso" }, IntensityPercent: 70u));

        var snapshot = state.Snapshot();
        snapshot.Should().ContainKey(("fake-owo", "pectoral_l"));
        snapshot.Should().ContainKey(("fake-owo", "pectoral_r"));
        snapshot.Should().ContainKey(("fake-owo", "abdominal_l"));
        snapshot.Should().ContainKey(("fake-owo", "abdominal_r"));
        snapshot[("fake-owo", "pectoral_l")].IsActive.Should().BeTrue();
        snapshot[("fake-owo", "abdominal_r")].IsActive.Should().BeTrue();
        snapshot.Should().NotContainKey(("fake-owo", "torso"),
            "the group id itself must NOT appear — the renderer iterates leaves only");
    }

    /// <summary>
    /// CancelOldest backends produce this exact race: trigger 1 starts,
    /// trigger 2 arrives, coordinator cancels trigger 1's CTS, trigger 2
    /// dispatches and emits SensationStarted, then trigger 1's playback
    /// finishes its cancellation and emits SensationCancelled for s1.
    /// Pre-fix the cancellation cleared every active zone on the backend
    /// — including trigger 2's freshly-stamped zones. Post-fix the
    /// stored ActiveSensationId on each zone filters the cancel so only
    /// zones that still belong to s1 lose their active flag.
    /// </summary>
    [Fact]
    public async Task Stale_cancellation_does_not_clear_a_newer_fires_zones()
    {
        var (state, bus, time, _) = NewState();
        await state.StartAsync(default);
        await using var __ = new AsyncDisposable(() => state.StopAsync(default));

        // First sensation: s1 fires on pectoral_l.
        await PublishAndAwait(state, bus, new SensationStarted(
            "mock-owo", time.GetUtcNow(), "s1", "test", "trace1",
            ZoneIds: new[] { "pectoral_l" }, IntensityPercent: 60u));

        // CancelOldest replacement: s2 fires on the same zone, stamping
        // ActiveSensationId = "s2".
        await PublishAndAwait(state, bus, new SensationStarted(
            "mock-owo", time.GetUtcNow(), "s2", "test", "trace2",
            ZoneIds: new[] { "pectoral_l" }, IntensityPercent: 60u));

        // The stale cancellation for s1 lands AFTER s2 already replaced
        // s1 on the zone. Pre-fix this cleared pectoral_l. Post-fix the
        // cancel matches no zones (none own SensationId="s1" anymore)
        // so StateChanged does NOT fire — that's the proof. Publish
        // directly with a short drain delay; PublishAndAwait would
        // (correctly) time out here.
        var stateChangeCount = 0;
        state.StateChanged += () => Interlocked.Increment(ref stateChangeCount);

        bus.Publish(new SensationCancelled(
            "mock-owo", time.GetUtcNow(), "s1", "test", "trace1", Reason: "preempted"));

        await Task.Delay(100);

        state.Snapshot()[("mock-owo", "pectoral_l")].IsActive.Should().BeTrue(
            "the newer s2 owns this zone; a late cancel for s1 must not deactivate it");
        stateChangeCount.Should().Be(0,
            "a cancellation that matches no zones must not flap StateChanged");
    }

    /// <summary>
    /// Sanity-check that the SensationId scoping doesn't break the
    /// normal completion path: a matching Completed for the active
    /// sensation clears the zone as before.
    /// </summary>
    [Fact]
    public async Task SensationCompleted_for_active_sensation_id_clears_the_zone()
    {
        var (state, bus, time, _) = NewState();
        await state.StartAsync(default);
        await using var __ = new AsyncDisposable(() => state.StopAsync(default));

        await PublishAndAwait(state, bus, new SensationStarted(
            "mock-owo", time.GetUtcNow(), "s1", "test", "trace",
            ZoneIds: new[] { "pectoral_l" }, IntensityPercent: 60u));
        await PublishAndAwait(state, bus, new SensationCompleted(
            "mock-owo", time.GetUtcNow(), "s1", "test", "trace"));

        state.Snapshot()[("mock-owo", "pectoral_l")].IsActive.Should().BeFalse();
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

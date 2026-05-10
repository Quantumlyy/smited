using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Smited.Daemon.Backends.Internal;
using Smited.Daemon.Backends.Mock;
using Smited.V1;
using Xunit;
using ParameterValue = Smited.Daemon.Backends.Internal.ParameterValue;
using MicrosensationParameters = Smited.Daemon.Backends.Internal.MicrosensationParameters;

namespace Smited.Daemon.Tests.Backends;

public class MockBhapticsBackendTests
{
    private static MockBhapticsBackend NewBackend(out FakeTimeProvider time)
    {
        time = new FakeTimeProvider(new DateTimeOffset(2026, 5, 9, 12, 0, 0, TimeSpan.Zero));
        return new MockBhapticsBackend(time, NullLogger<MockBhapticsBackend>.Instance);
    }

    [Fact]
    public void Static_descriptors_match_the_spec()
    {
        var backend = NewBackend(out _);

        backend.Id.Should().Be("mock-bhaptics");
        backend.Kind.Should().Be("bhaptics_tactsuit");
        backend.DisplayName.Should().Be("Mock TactSuit X40");
        backend.Status.Should().Be(BackendStatus.Ready);
        backend.Capabilities.Should().BeEquivalentTo(
            "vibration",
            "zoned",
            "wireless",
            "configurable_intensity",
            "concurrent_sensations",
            "sensation_registry_mutable");
        backend.Capabilities.Should().NotContain("calibrated");
        backend.Capabilities.Should().NotContain("ems");
        backend.Concurrency.MaxConcurrent.Should().Be(4u);
        backend.Concurrency.Policy.Should().Be(ConcurrencyPolicy.Priority);
        backend.Calibration.Should().BeNull();
    }

    [Fact]
    public void Zone_topology_includes_40_vest_zones_and_six_groups_without_accessories()
    {
        var backend = NewBackend(out _);

        backend.Zones.Zones.Should().HaveCount(40);
        backend.Zones.Zones.Select(z => z.Id).Should()
            .Contain("vest_front_0").And.Contain("vest_front_19")
            .And.Contain("vest_back_0").And.Contain("vest_back_19");

        backend.Zones.Zones.Where(z => z.Id.StartsWith("vest_front_"))
            .Should().AllSatisfy(z => z.Position.Frame.Should().Be("body_front"));
        backend.Zones.Zones.Where(z => z.Id.StartsWith("vest_back_"))
            .Should().AllSatisfy(z => z.Position.Frame.Should().Be("body_back"));

        backend.Zones.Groups.Select(g => g.Id).Should().BeEquivalentTo(
            "front", "back", "front_chest", "back_shoulders", "torso", "all");
    }

    [Fact]
    public void Position_hint_for_vest_front_uses_grid_coordinates()
    {
        var backend = NewBackend(out _);

        // vest_front_7 → column 3, row 1 in the 4×5 grid →
        // x = 3/3 = 1.0, y = 1/4 = 0.25.
        var zone = backend.Zones.Zones.Single(z => z.Id == "vest_front_7");
        zone.Position.X.Should().BeApproximately(1.0f, 0.001f);
        zone.Position.Y.Should().BeApproximately(0.25f, 0.001f);
        zone.Position.Z.Should().Be(0f);
        zone.Position.Frame.Should().Be("body_front");
    }

    [Fact]
    public void Parameter_schema_has_three_params_with_correct_ranges()
    {
        var backend = NewBackend(out _);

        var byName = backend.Parameters.Parameters.ToDictionary(p => p.Name);
        byName.Should().HaveCount(3);

        byName["intensity"].Type.Should().Be(ParameterType.Number);
        byName["intensity"].Required.Should().BeTrue();
        byName["intensity"].Min.Should().Be(0);
        byName["intensity"].Max.Should().Be(100);
        byName["intensity"].Unit.Should().Be("%");

        byName["duration"].Type.Should().Be(ParameterType.Duration);
        byName["duration"].Required.Should().BeTrue();
        byName["duration"].Min.Should().Be(0);
        byName["duration"].Max.Should().Be(10);

        byName["frequency"].Type.Should().Be(ParameterType.Number);
        byName["frequency"].Required.Should().BeFalse();
        byName["frequency"].Min.Should().Be(50);
        byName["frequency"].Max.Should().Be(200);
        byName["frequency"].Unit.Should().Be("Hz");
    }

    [Fact]
    public async Task SetAccessoriesPresent_true_expands_topology_with_glove_and_sleeve_zones()
    {
        var backend = NewBackend(out _);
        await using var ____ = backend;

        ((IMockBhapticsController)backend).SetAccessoriesPresent(true);

        backend.Zones.Zones.Should().HaveCount(40 + 12 + 8);
        backend.Zones.Zones.Select(z => z.Id).Should()
            .Contain("glove_l_0").And.Contain("glove_r_5")
            .And.Contain("arm_l_0").And.Contain("arm_r_3");
        backend.Zones.Groups.Select(g => g.Id).Should().Contain("gloves").And.Contain("arms");

        var allGroup = backend.Zones.Groups.Single(g => g.Id == "all");
        allGroup.ZoneIds.Should().HaveCount(60);
    }

    [Fact]
    public async Task SetAccessoriesPresent_emits_BackendLifecycleEvent_with_StatusChanged()
    {
        var backend = NewBackend(out _);
        await using var ____ = backend;

        ((IMockBhapticsController)backend).SetAccessoriesPresent(true);

        var enumerator = backend.Events.GetAsyncEnumerator();
        var evt = await NextWithin(enumerator, TimeSpan.FromSeconds(1));
        var lifecycle = evt.Should().BeOfType<BackendLifecycleEvent>().Subject;
        lifecycle.Change.Should().Be(BackendLifecycleChange.StatusChanged);
        lifecycle.Snapshot.Id.Should().Be("mock-bhaptics");
    }

    [Fact]
    public async Task EmitStatusChange_emits_BackendLifecycleEvent_with_supplied_status_and_reason()
    {
        var backend = NewBackend(out _);
        await using var ____ = backend;

        ((IMockBhapticsController)backend).EmitStatusChange(BackendStatus.Degraded, "test_reason");

        backend.Status.Should().Be(BackendStatus.Degraded);

        var enumerator = backend.Events.GetAsyncEnumerator();
        var evt = await NextWithin(enumerator, TimeSpan.FromSeconds(1));
        var lifecycle = evt.Should().BeOfType<BackendLifecycleEvent>().Subject;
        lifecycle.Change.Should().Be(BackendLifecycleChange.StatusChanged);
        lifecycle.Snapshot.Status.Should().Be(BackendStatus.Degraded);
        lifecycle.Reason.Should().Be("test_reason");
    }

    [Fact]
    public async Task Multi_microsensation_request_sums_durations_for_estimated_total()
    {
        var backend = NewBackend(out _);
        await using var ____ = backend;

        var request = new BackendTriggerRequest(
            SensationId: "multi",
            SensationName: "two_pulses",
            ZoneIds: new[] { "front" },
            IntensityScale: null,
            Priority: 0,
            ClientTraceId: "trace",
            Microsensations: new[]
            {
                new MicrosensationParameters(new Dictionary<string, ParameterValue>
                {
                    ["intensity"] = new ParameterValue.Number(50),
                    ["duration"] = new ParameterValue.Duration(TimeSpan.FromMilliseconds(200)),
                }),
                new MicrosensationParameters(new Dictionary<string, ParameterValue>
                {
                    ["intensity"] = new ParameterValue.Number(50),
                    ["duration"] = new ParameterValue.Duration(TimeSpan.FromMilliseconds(200)),
                }),
            });

        var result = await backend.TriggerAsync(request, CancellationToken.None);

        result.EstimatedDuration.Should().Be(TimeSpan.FromMilliseconds(400));
    }

    [Fact]
    public async Task Trigger_emits_Started_then_Completed_after_estimated_duration()
    {
        var backend = NewBackend(out var time);
        await using var ____ = backend;

        await backend.TriggerAsync(MakeRequest("s1", TimeSpan.FromSeconds(2)), CancellationToken.None);

        var enumerator = backend.Events.GetAsyncEnumerator();

        (await NextWithin(enumerator, TimeSpan.FromSeconds(1))).Should().BeOfType<SensationStarted>();

        time.Advance(TimeSpan.FromSeconds(2));

        (await NextWithin(enumerator, TimeSpan.FromSeconds(1))).Should().BeOfType<SensationCompleted>();
        backend.ActiveSensationIds.Should().BeEmpty();
    }

    [Fact]
    public async Task Two_concurrent_triggers_within_capacity_both_run_to_completion()
    {
        var backend = NewBackend(out var time);
        await using var ____ = backend;

        await backend.TriggerAsync(MakeRequest("s1", TimeSpan.FromSeconds(1)), CancellationToken.None);
        await backend.TriggerAsync(MakeRequest("s2", TimeSpan.FromSeconds(1)), CancellationToken.None);

        backend.ActiveSensationIds.Should().HaveCount(2);

        var enumerator = backend.Events.GetAsyncEnumerator();
        var ev1 = await NextWithin(enumerator, TimeSpan.FromSeconds(1));
        var ev2 = await NextWithin(enumerator, TimeSpan.FromSeconds(1));
        new[] { ev1, ev2 }.Should().AllBeOfType<SensationStarted>();

        time.Advance(TimeSpan.FromSeconds(1));

        var ev3 = await NextWithin(enumerator, TimeSpan.FromSeconds(1));
        var ev4 = await NextWithin(enumerator, TimeSpan.FromSeconds(1));
        new[] { ev3, ev4 }.Should().AllBeOfType<SensationCompleted>();

        backend.ActiveSensationIds.Should().BeEmpty();
    }

    [Fact]
    public async Task Stop_with_specific_id_cancels_only_that_sensation()
    {
        var backend = NewBackend(out _);
        await using var ____ = backend;

        await backend.TriggerAsync(MakeRequest("s1", TimeSpan.FromSeconds(5)), CancellationToken.None);
        await backend.TriggerAsync(MakeRequest("s2", TimeSpan.FromSeconds(5)), CancellationToken.None);

        var stopped = await backend.StopAsync(new BackendStopRequest("s1", All: false), CancellationToken.None);
        stopped.Should().Be(1);
        backend.ActiveSensationIds.Should().BeEquivalentTo("s2");
    }

    [Fact]
    public async Task Stop_with_all_cancels_every_active_sensation_and_returns_count()
    {
        var backend = NewBackend(out _);
        await using var ____ = backend;

        await backend.TriggerAsync(MakeRequest("s1", TimeSpan.FromSeconds(5)), CancellationToken.None);
        await backend.TriggerAsync(MakeRequest("s2", TimeSpan.FromSeconds(5)), CancellationToken.None);
        await backend.TriggerAsync(MakeRequest("s3", TimeSpan.FromSeconds(5)), CancellationToken.None);

        var stopped = await backend.StopAsync(new BackendStopRequest(SensationId: null, All: true), CancellationToken.None);
        stopped.Should().Be(3);
        backend.ActiveSensationIds.Should().BeEmpty();
    }

    private static BackendTriggerRequest MakeRequest(string id, TimeSpan duration)
    {
        var values = new Dictionary<string, ParameterValue>
        {
            ["intensity"] = new ParameterValue.Number(50),
            ["duration"] = new ParameterValue.Duration(duration),
        };
        return new BackendTriggerRequest(
            SensationId: id,
            SensationName: "test",
            ZoneIds: new[] { "front" },
            IntensityScale: null,
            Priority: 0,
            ClientTraceId: "trace",
            Microsensations: new[] { new MicrosensationParameters(values) });
    }

    private static async Task<BackendEvent> NextWithin(
        IAsyncEnumerator<BackendEvent> enumerator,
        TimeSpan timeout)
    {
        var task = enumerator.MoveNextAsync().AsTask();
        var winner = await Task.WhenAny(task, Task.Delay(timeout));
        if (winner != task)
        {
            throw new TimeoutException($"No event in {timeout}");
        }
        var ok = await task;
        if (!ok) throw new InvalidOperationException("Stream completed unexpectedly");
        return enumerator.Current;
    }
}

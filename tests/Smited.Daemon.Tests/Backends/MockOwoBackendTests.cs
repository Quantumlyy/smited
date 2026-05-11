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

public class MockOwoBackendTests
{
    private static MockOwoBackend NewBackend(out FakeTimeProvider time)
    {
        time = new FakeTimeProvider(new DateTimeOffset(2026, 5, 9, 12, 0, 0, TimeSpan.Zero));
        return new MockOwoBackend(time, NullLogger<MockOwoBackend>.Instance);
    }

    [Fact]
    public void Static_descriptors_match_the_spec()
    {
        var backend = NewBackend(out _);

        backend.Id.Should().Be("mock-owo");
        backend.Kind.Should().Be("owo_skin");
        backend.DisplayName.Should().Be("Mock OWO Skin");
        backend.Status.Should().Be(BackendStatus.Ready);
        backend.Capabilities.Should().BeEquivalentTo(
            "ems", "zoned", "calibrated", "sensation_registry_mutable");
        backend.Concurrency.MaxConcurrent.Should().Be(1u);
        backend.Concurrency.Policy.Should().Be(ConcurrencyPolicy.CancelOldest);
        backend.Calibration!.Calibrated.Should().BeTrue();
    }

    [Fact]
    public void Zone_topology_includes_10_zones_and_3_groups()
    {
        var backend = NewBackend(out _);

        backend.Zones.Zones.Select(z => z.Id).Should().BeEquivalentTo(
            "pectoral_l", "pectoral_r",
            "abdominal_l", "abdominal_r",
            "lumbar_l", "lumbar_r",
            "dorsal_l", "dorsal_r",
            "arm_l", "arm_r");
        backend.Zones.Groups.Select(g => g.Id).Should().BeEquivalentTo("torso", "arms", "all");
    }

    [Fact]
    public void Parameter_schema_lists_six_parameters_with_correct_types()
    {
        var backend = NewBackend(out _);

        var byName = backend.Parameters.Parameters.ToDictionary(p => p.Name);
        byName.Should().HaveCount(6);
        byName["frequency"].Type.Should().Be(ParameterType.Number);
        byName["frequency"].Min.Should().Be(1);
        byName["frequency"].Max.Should().Be(100);
        byName["intensity"].Type.Should().Be(ParameterType.Number);
        byName["duration"].Type.Should().Be(ParameterType.Duration);
        byName["ramp_up"].Type.Should().Be(ParameterType.Duration);
        byName["ramp_down"].Type.Should().Be(ParameterType.Duration);
        byName["exit_delay"].Type.Should().Be(ParameterType.Duration);

        byName["frequency"].Required.Should().BeTrue();
        byName["intensity"].Required.Should().BeTrue();
        byName["duration"].Required.Should().BeTrue();
        byName["ramp_up"].Required.Should().BeFalse();
        byName["ramp_down"].Required.Should().BeFalse();
        byName["exit_delay"].Required.Should().BeFalse();
    }

    [Fact]
    public async Task Multi_microsensation_request_sums_durations_for_estimated_total()
    {
        var backend = NewBackend(out _);
        await using var ____ = backend;

        // Two pulses of 0.3s each — the total wall-clock time should be
        // the sum, not the max, otherwise the second pulse never gets
        // its slot and the SensationCompleted fires too early.
        var values = new Dictionary<ParameterValue, ParameterValue>(0); // placeholder type-only
        var values1 = new Dictionary<string, ParameterValue>
        {
            ["frequency"] = new ParameterValue.Number(50),
            ["intensity"] = new ParameterValue.Number(50),
            ["duration"] = new ParameterValue.Duration(TimeSpan.FromMilliseconds(300)),
        };
        var values2 = new Dictionary<string, ParameterValue>
        {
            ["frequency"] = new ParameterValue.Number(50),
            ["intensity"] = new ParameterValue.Number(50),
            ["duration"] = new ParameterValue.Duration(TimeSpan.FromMilliseconds(400)),
        };
        var request = new BackendTriggerRequest(
            SensationId: "multi",
            SensationName: "two_pulses",
            ZoneIds: new[] { "pectoral_l" },
            IntensityScale: null,
            Priority: 0,
            ClientTraceId: "trace",
            Microsensations: new[]
            {
                new MicrosensationParameters(values1),
                new MicrosensationParameters(values2),
            });

        var result = await backend.TriggerAsync(request, CancellationToken.None);

        result.EstimatedDuration.Should().Be(TimeSpan.FromMilliseconds(700));
    }

    [Fact]
    public async Task Trigger_emits_Started_then_Completed_after_estimated_duration()
    {
        var backend = NewBackend(out var time);
        await using var ____ = backend;

        var request = MakeRequest("s1", duration: TimeSpan.FromSeconds(2));

        var result = await backend.TriggerAsync(request, CancellationToken.None);
        result.EstimatedDuration.Should().Be(TimeSpan.FromSeconds(2));

        var enumerator = backend.Events.GetAsyncEnumerator();

        (await NextWithin(enumerator, TimeSpan.FromSeconds(1))).Should().BeOfType<SensationStarted>();

        time.Advance(TimeSpan.FromSeconds(2));

        (await NextWithin(enumerator, TimeSpan.FromSeconds(1))).Should().BeOfType<SensationCompleted>();
        backend.ActiveSensationIds.Should().BeEmpty();
    }

    [Fact]
    public async Task SensationStarted_event_carries_request_zones_and_intensity()
    {
        var backend = NewBackend(out _);
        await using var ____ = backend;

        var values = new Dictionary<string, ParameterValue>
        {
            ["frequency"] = new ParameterValue.Number(50),
            ["intensity"] = new ParameterValue.Number(50),
            ["duration"] = new ParameterValue.Duration(TimeSpan.FromMilliseconds(500)),
        };
        var request = new BackendTriggerRequest(
            SensationId: "scoped",
            SensationName: "diag",
            ZoneIds: new[] { "pectoral_l", "abdominal_r" },
            IntensityScale: 75u,
            Priority: 0,
            ClientTraceId: "trace-zones",
            Microsensations: new[] { new MicrosensationParameters(values) });

        await backend.TriggerAsync(request, CancellationToken.None);

        var enumerator = backend.Events.GetAsyncEnumerator();
        var evt = await NextWithin(enumerator, TimeSpan.FromSeconds(1));
        var started = evt.Should().BeOfType<SensationStarted>().Which;

        started.ZoneIds.Should().Equal("pectoral_l", "abdominal_r");
        started.IntensityPercent.Should().Be(75u);
    }

    [Fact]
    public async Task Stop_cancels_active_sensation_and_emits_Cancelled()
    {
        var backend = NewBackend(out _);
        await using var ____ = backend;

        var request = MakeRequest("s1", duration: TimeSpan.FromSeconds(5));
        await backend.TriggerAsync(request, CancellationToken.None);

        var enumerator = backend.Events.GetAsyncEnumerator();
        (await NextWithin(enumerator, TimeSpan.FromSeconds(1))).Should().BeOfType<SensationStarted>();

        var stopped = await backend.StopAsync(new BackendStopRequest("s1", All: false), CancellationToken.None);
        stopped.Should().Be(1);

        (await NextWithin(enumerator, TimeSpan.FromSeconds(1))).Should().BeOfType<SensationCancelled>();
    }

    [Fact]
    public async Task Stop_with_all_cancels_every_active_sensation()
    {
        var backend = NewBackend(out _);
        await using var ____ = backend;

        await backend.TriggerAsync(MakeRequest("s1", duration: TimeSpan.FromSeconds(5)), CancellationToken.None);

        backend.ActiveSensationIds.Should().HaveCount(1);
        var stopped = await backend.StopAsync(new BackendStopRequest(SensationId: null, All: true), CancellationToken.None);
        stopped.Should().Be(1);
    }

    [Fact]
    public async Task SetCalibrated_flips_state_and_emits_CalibrationChanged()
    {
        var backend = NewBackend(out var time);
        await using var ____ = backend;

        ((IMockOwoController)backend).SetCalibrated(false, time.GetUtcNow());

        var enumerator = backend.Events.GetAsyncEnumerator();
        var evt = await NextWithin(enumerator, TimeSpan.FromSeconds(1));
        evt.Should().BeOfType<CalibrationChangedEvent>()
            .Which.NewState.Calibrated.Should().BeFalse();

        backend.Calibration!.Calibrated.Should().BeFalse();
    }

    private static BackendTriggerRequest MakeRequest(string id, TimeSpan duration)
    {
        var values = new Dictionary<string, ParameterValue>
        {
            ["frequency"] = new ParameterValue.Number(50),
            ["intensity"] = new ParameterValue.Number(50),
            ["duration"] = new ParameterValue.Duration(duration),
        };
        return new BackendTriggerRequest(
            SensationId: id,
            SensationName: "test",
            ZoneIds: new[] { "pectoral_l" },
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

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Smited.Daemon.Backends;
using Smited.Daemon.Backends.Internal;
using Smited.Daemon.BodyMap;
using Smited.Daemon.Pishock;
using Smited.V1;
using Xunit;
using ParameterValue = Smited.Daemon.Backends.Internal.ParameterValue;

namespace Smited.Daemon.Tests.Backends.Pishock;

public class MockPishockBackendTests
{
    private static MockPishockBackend NewBackend(
        out FakeTimeProvider time,
        PishockBackendOptions? options = null,
        string id = "pishock-test")
    {
        time = new FakeTimeProvider(new DateTimeOffset(2026, 5, 10, 12, 0, 0, TimeSpan.Zero));
        var backend = new MockPishockBackend(
            id,
            options ?? new PishockBackendOptions(),
            time,
            NullLogger<MockPishockBackend>.Instance);
        return backend;
    }

    [Fact]
    public void Static_descriptors_match_the_spec()
    {
        var backend = NewBackend(out _, id: "left-thigh");

        backend.Id.Should().Be("left-thigh");
        backend.Kind.Should().Be("pishock");
        backend.Status.Should().Be(BackendStatus.Ready);
        backend.Calibration.Should().BeNull(
            "PiShock has no calibration phase — safety lives in the per-trigger intensity ceiling");
    }

    [Fact]
    public void DisplayName_falls_back_to_descriptor_id_when_options_unset()
    {
        var backend = NewBackend(out _, id: "left-thigh");
        backend.DisplayName.Should().Be("left-thigh");
    }

    [Fact]
    public void DisplayName_uses_options_value_when_set()
    {
        var options = new PishockBackendOptions { DisplayName = "Left thigh" };
        var backend = NewBackend(out _, options, id: "left-thigh");
        backend.DisplayName.Should().Be("Left thigh");
    }

    [Fact]
    public void Capabilities_excludes_disallowed_ops()
    {
        var options = new PishockBackendOptions
        {
            AllowedOps = new() { PishockOp.Vibrate, PishockOp.Beep },
        };
        var backend = NewBackend(out _, options);

        backend.Capabilities.Should().BeEquivalentTo(
            "pishock", "vibrate", "beep", "ratelimited");
        backend.Capabilities.Should().NotContain("shock");
    }

    [Fact]
    public void Capabilities_includes_shock_when_allowed()
    {
        var options = new PishockBackendOptions
        {
            AllowedOps = new() { PishockOp.Vibrate, PishockOp.Beep, PishockOp.Shock },
        };
        var backend = NewBackend(out _, options);

        backend.Capabilities.Should().BeEquivalentTo(
            "pishock", "vibrate", "beep", "shock", "ratelimited");
    }

    [Fact]
    public void Zone_topology_is_a_single_zone_named_shock_with_no_groups()
    {
        var backend = NewBackend(out _);

        backend.Zones.Zones.Should().HaveCount(1);
        backend.Zones.Zones[0].Id.Should().Be("shock");
        backend.Zones.Groups.Should().BeEmpty();
    }

    [Fact]
    public void Parameter_schema_advertises_every_op_regardless_of_AllowedOps()
    {
        // Kind-scoped bundled sensations (sensations/pishock/*.json)
        // validate against EVERY registered backend of the pishock
        // kind. Narrowing the schema's enum_values to a descriptor's
        // AllowedOps would refuse a bundled Beep sensation on a
        // vibrate-only descriptor at startup, even though the user
        // simply doesn't want to fire Beep on that shocker. The
        // schema stays broad; the per-instance AllowedOps gate runs
        // at trigger time in PishockTriggerValidator with a
        // structured INVALID_PARAMETER.
        var options = new PishockBackendOptions
        {
            AllowedOps = new() { PishockOp.Vibrate },
        };
        var backend = NewBackend(out _, options);

        var byName = backend.Parameters.Parameters.ToDictionary(p => p.Name);
        byName["op"].EnumValues.Should().BeEquivalentTo(new[] { "vibrate", "beep", "shock" });
    }

    [Fact]
    public void Parameter_schema_declares_op_duration_intensity_and_optional_delay_before()
    {
        var backend = NewBackend(out _);

        var byName = backend.Parameters.Parameters.ToDictionary(p => p.Name);

        byName.Should().ContainKey("op").WhoseValue.Type.Should().Be(ParameterType.Enum);
        byName["op"].Required.Should().BeTrue();
        // Lowercase: ParameterValue.enum_value is protovalidated as a
        // lowercase ident; Pascal "Vibrate" is rejected on the wire
        // before reaching schema validation. The backend's runtime
        // op-name parse is case-insensitive so existing test fixtures
        // that constructed "Vibrate" still resolve via Enum.TryParse.
        byName["op"].EnumValues.Should().Contain("vibrate");
        byName["op"].EnumValues.Should().Contain("beep");

        byName.Should().ContainKey("duration").WhoseValue.Type.Should().Be(ParameterType.Duration);
        byName["duration"].Required.Should().BeTrue();

        byName.Should().ContainKey("intensity").WhoseValue.Type.Should().Be(ParameterType.Number);
        byName["intensity"].Required.Should().BeTrue();

        byName.Should().ContainKey("delay_before").WhoseValue.Type.Should().Be(ParameterType.Duration);
        byName["delay_before"].Required.Should().BeFalse();
    }

    [Fact]
    public void Concurrency_is_single_channel_with_reject_new_policy()
    {
        // CancelOldest would only cancel the daemon's local Task.Delay;
        // PiShock's wire protocol has no "cancel an in-progress op"
        // message so the device keeps firing the previous pulse, and a
        // follow-up trigger sends a second op while the first is still
        // active. RejectNew makes MaxConcurrent=1 actually mean "one
        // op at a time on this device" — overlapping triggers get a
        // structured RATE_LIMITED instead of silently double-firing.
        var backend = NewBackend(out _);

        backend.Concurrency.MaxConcurrent.Should().Be(1u);
        backend.Concurrency.Policy.Should().Be(ConcurrencyPolicy.RejectNew);
    }

    [Fact]
    public void ForbiddenRegions_matches_the_manufacturer_chart_plus_head_face()
    {
        var backend = NewBackend(out _);

        backend.ForbiddenRegions.Should().BeEquivalentTo(new[]
        {
            BodyRegion.Head,
            BodyRegion.Face,
            BodyRegion.Throat,
            BodyRegion.Neck,
            BodyRegion.ChestFront,
            BodyRegion.ChestOverHeart,
            BodyRegion.BackUpper,
            BodyRegion.BackLower,
        });
    }

    [Fact]
    public async Task TriggerAsync_with_disallowed_op_throws_BackendTriggerRejected_invalid_parameter()
    {
        var options = new PishockBackendOptions
        {
            AllowedOps = new() { PishockOp.Vibrate },
        };
        var backend = NewBackend(out _, options);
        await using var __ = backend;

        var request = MakeRequest(op: PishockOp.Shock, durationMs: 200, intensity: 10);

        var act = async () => await backend.TriggerAsync(request, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<BackendTriggerRejectedException>();
        ex.Which.Code.Should().Be(TriggerErrorCode.InvalidParameter);
        ex.Which.Field.Should().Contain("op");
        ex.Which.Message.Should().Contain("Shock");
    }

    [Fact]
    public async Task TriggerAsync_with_intensity_above_shock_cap_throws_invalid_parameter()
    {
        var options = new PishockBackendOptions
        {
            AllowedOps = new() { PishockOp.Shock, PishockOp.Vibrate },
            MaxIntensityShock = 25,
            MaxIntensityVibrate = 100,
        };
        var backend = NewBackend(out _, options);
        await using var __ = backend;

        var request = MakeRequest(op: PishockOp.Shock, durationMs: 200, intensity: 30);

        var act = async () => await backend.TriggerAsync(request, CancellationToken.None);
        var ex = await act.Should().ThrowAsync<BackendTriggerRejectedException>();
        ex.Which.Code.Should().Be(TriggerErrorCode.InvalidParameter);
        ex.Which.Field.Should().Contain("intensity");
    }

    [Fact]
    public async Task TriggerAsync_with_intensity_above_vibrate_cap_throws_invalid_parameter()
    {
        var options = new PishockBackendOptions { MaxIntensityVibrate = 50 };
        var backend = NewBackend(out _, options);
        await using var __ = backend;

        var request = MakeRequest(op: PishockOp.Vibrate, durationMs: 200, intensity: 60);

        var act = async () => await backend.TriggerAsync(request, CancellationToken.None);
        var ex = await act.Should().ThrowAsync<BackendTriggerRejectedException>();
        ex.Which.Code.Should().Be(TriggerErrorCode.InvalidParameter);
        ex.Which.Field.Should().Contain("intensity");
    }

    [Fact]
    public async Task TriggerAsync_with_duration_above_cap_throws_invalid_parameter()
    {
        var options = new PishockBackendOptions { MaxDurationMs = 500 };
        var backend = NewBackend(out _, options);
        await using var __ = backend;

        var request = MakeRequest(op: PishockOp.Vibrate, durationMs: 1000, intensity: 30);

        var act = async () => await backend.TriggerAsync(request, CancellationToken.None);
        var ex = await act.Should().ThrowAsync<BackendTriggerRejectedException>();
        ex.Which.Code.Should().Be(TriggerErrorCode.InvalidParameter);
        ex.Which.Field.Should().Contain("duration");
    }

    [Fact]
    public async Task TriggerAsync_throws_RATE_LIMITED_after_burst_capacity_exhausted()
    {
        var options = new PishockBackendOptions
        {
            MaxBurst = 2,
            MaxOpsPerSecond = 1,
        };
        var backend = NewBackend(out _, options);
        await using var __ = backend;

        await backend.TriggerAsync(
            MakeRequest(PishockOp.Vibrate, 100, 30),
            CancellationToken.None);
        await backend.TriggerAsync(
            MakeRequest(PishockOp.Vibrate, 100, 30),
            CancellationToken.None);

        var act = async () => await backend.TriggerAsync(
            MakeRequest(PishockOp.Vibrate, 100, 30),
            CancellationToken.None);

        var ex = await act.Should().ThrowAsync<BackendTriggerRejectedException>();
        ex.Which.Code.Should().Be(TriggerErrorCode.RateLimited);
    }

    [Fact]
    public async Task Rate_limiter_refills_tokens_at_configured_rate()
    {
        var options = new PishockBackendOptions
        {
            MaxBurst = 1,
            MaxOpsPerSecond = 1,
        };
        var backend = NewBackend(out var time, options);
        await using var __ = backend;

        await backend.TriggerAsync(
            MakeRequest(PishockOp.Vibrate, 100, 30),
            CancellationToken.None);

        var rejected = async () => await backend.TriggerAsync(
            MakeRequest(PishockOp.Vibrate, 100, 30),
            CancellationToken.None);
        await rejected.Should().ThrowAsync<BackendTriggerRejectedException>();

        time.Advance(TimeSpan.FromSeconds(1));

        var nextResult = await backend.TriggerAsync(
            MakeRequest(PishockOp.Vibrate, 100, 30),
            CancellationToken.None);
        nextResult.Should().NotBeNull();
    }

    [Fact]
    public async Task TriggerAsync_emits_Started_then_Completed_after_duration()
    {
        // LAN mode preserves millisecond timing on the wire; Cloud
        // would round 500ms up to 1s and the test would observe 1s
        // instead of 500ms. The cloud-rounding case is covered by
        // the dedicated test below.
        var backend = NewBackend(out var time, new PishockBackendOptions
        {
            Mode = PishockTransportMode.Lan,
            DeviceIp = "192.168.1.50",
        });
        await using var __ = backend;

        var request = MakeRequest(PishockOp.Vibrate, durationMs: 500, intensity: 30);
        var result = await backend.TriggerAsync(request, CancellationToken.None);
        result.EstimatedDuration.Should().Be(TimeSpan.FromMilliseconds(500));

        var enumerator = backend.Events.GetAsyncEnumerator();
        var started = await NextWithin(enumerator, TimeSpan.FromSeconds(1));
        started.Should().BeOfType<SensationStarted>();

        time.Advance(TimeSpan.FromMilliseconds(500));
        var completed = await NextWithin(enumerator, TimeSpan.FromSeconds(1));
        completed.Should().BeOfType<SensationCompleted>();
    }

    [Fact]
    public async Task Sequence_of_microsensations_sums_durations_plus_delay_before()
    {
        var backend = NewBackend(out _, new PishockBackendOptions
        {
            Mode = PishockTransportMode.Lan,
            DeviceIp = "192.168.1.50",
        });
        await using var __ = backend;

        var request = MakeSequenceRequest(
            (PishockOp.Vibrate, 100, 50, delayBeforeMs: 0),
            (PishockOp.Vibrate, 100, 50, delayBeforeMs: 200),
            (PishockOp.Vibrate, 100, 50, delayBeforeMs: 200));

        var result = await backend.TriggerAsync(request, CancellationToken.None);

        // 100 + (200+100) + (200+100) = 700ms (LAN, no rounding)
        result.EstimatedDuration.Should().Be(TimeSpan.FromMilliseconds(700));
    }

    [Fact]
    public async Task TriggerAsync_in_cloud_mode_rounds_estimated_duration_up_to_seconds()
    {
        // Mock simulates the wire reality so behavior is consistent
        // with the real backend on the same options. A 100ms cloud
        // vibrate is reported as a 1s sensation because that's how
        // long the device fires for.
        var backend = NewBackend(out _, new PishockBackendOptions
        {
            Mode = PishockTransportMode.Cloud,
            Username = "u", ApiKey = "k", ShareCode = "s",
        });
        await using var __ = backend;

        var result = await backend.TriggerAsync(
            MakeRequest(PishockOp.Vibrate, durationMs: 100, intensity: 30),
            CancellationToken.None);

        result.EstimatedDuration.Should().Be(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task StopAsync_returns_zero_documenting_pishock_protocol_has_no_cancel()
    {
        var options = new PishockBackendOptions { MaxDurationMs = 5000 };
        var backend = NewBackend(out _, options);
        await using var __ = backend;

        await backend.TriggerAsync(MakeRequest(PishockOp.Vibrate, 1000, 30), CancellationToken.None);

        var stopped = await backend.StopAsync(
            new BackendStopRequest(SensationId: null, All: true),
            CancellationToken.None);

        stopped.Should().Be(0,
            "PiShock's wire protocol has no 'cancel an in-progress op' message — "
            + "StopAsync is documented as a no-op that the daemon waits out");
    }

    private static BackendTriggerRequest MakeRequest(PishockOp op, int durationMs, int intensity)
    {
        var values = new Dictionary<string, ParameterValue>
        {
            ["op"] = new ParameterValue.EnumValue(op.ToString()),
            ["duration"] = new ParameterValue.Duration(TimeSpan.FromMilliseconds(durationMs)),
            ["intensity"] = new ParameterValue.Number(intensity),
        };
        return new BackendTriggerRequest(
            SensationId: $"trigger-{Guid.NewGuid():N}",
            SensationName: "test",
            ZoneIds: new[] { "shock" },
            IntensityScale: null,
            Priority: 0,
            ClientTraceId: "trace",
            Microsensations: new[] { new MicrosensationParameters(values) });
    }

    private static BackendTriggerRequest MakeSequenceRequest(
        params (PishockOp op, int durationMs, int intensity, int delayBeforeMs)[] steps)
    {
        var micros = steps.Select(step =>
        {
            var values = new Dictionary<string, ParameterValue>
            {
                ["op"] = new ParameterValue.EnumValue(step.op.ToString()),
                ["duration"] = new ParameterValue.Duration(TimeSpan.FromMilliseconds(step.durationMs)),
                ["intensity"] = new ParameterValue.Number(step.intensity),
            };
            if (step.delayBeforeMs > 0)
            {
                values["delay_before"] = new ParameterValue.Duration(
                    TimeSpan.FromMilliseconds(step.delayBeforeMs));
            }
            return new MicrosensationParameters(values);
        }).ToArray();

        return new BackendTriggerRequest(
            SensationId: $"trigger-{Guid.NewGuid():N}",
            SensationName: "sequence",
            ZoneIds: new[] { "shock" },
            IntensityScale: null,
            Priority: 0,
            ClientTraceId: "trace",
            Microsensations: micros);
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

// Excluded from compile on non-Windows hosts via the test csproj's
// conditional <Compile Remove>. References BhapticsVestBackend, which
// lives in the Windows-only Smited.Daemon.Bhaptics assembly.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Smited.Daemon.Backends;
using Smited.Daemon.Backends.Internal;
using Smited.Daemon.Bhaptics;
using Smited.V1;
using Xunit;
using ParameterValue = Smited.Daemon.Backends.Internal.ParameterValue;
using MicrosensationParameters = Smited.Daemon.Backends.Internal.MicrosensationParameters;

namespace Smited.Daemon.Tests.Backends;

public class BhapticsVestBackendTests
{
    private static BhapticsVestBackend NewBackend(
        out FakeTimeProvider time,
        out IBhapticsSdk sdk,
        BhapticsVestOptions? options = null,
        bool deviceConnected = true)
    {
        time = new FakeTimeProvider(new DateTimeOffset(2026, 5, 9, 12, 0, 0, TimeSpan.Zero));
        sdk = Substitute.For<IBhapticsSdk>();
        sdk.IsDeviceConnected("vest").Returns(deviceConnected);
        return new BhapticsVestBackend(
            options ?? new BhapticsVestOptions(),
            sdk,
            time,
            NullLogger<BhapticsVestBackend>.Instance);
    }

    [Fact]
    public void Static_descriptors_match_the_spec()
    {
        var backend = NewBackend(out _, out _);

        backend.Id.Should().Be("bhaptics-vest");
        backend.Kind.Should().Be("bhaptics_vest");
        backend.DeviceKey.Should().Be("vest");
        backend.MotorCount.Should().Be(40);
        backend.DisplayName.Should().Be("bHaptics TactSuit");
        backend.Status.Should().Be(BackendStatus.Disconnected);
        backend.Capabilities.Should().BeEquivalentTo("vibrotactile", "zoned", "calibrated");
        backend.Capabilities.Should().NotContain("ems",
            "bHaptics is vibrotactile, not EMS — the ems capability is OWO's");
        backend.Concurrency.MaxConcurrent.Should().Be(1u);
        backend.Concurrency.Policy.Should().Be(ConcurrencyPolicy.CancelOldest);
        backend.Calibration.Should().BeNull();
    }

    [Fact]
    public void Parameter_schema_omits_frequency()
    {
        var backend = NewBackend(out _, out _);
        var names = backend.Parameters.Parameters.Select(p => p.Name).ToList();

        names.Should().BeEquivalentTo(
            "intensity", "duration", "ramp_up", "ramp_down", "exit_delay");
        names.Should().NotContain("frequency",
            "addendum #3: bHaptics is vibrotactile, no frequency parameter");
    }

    [Fact]
    public void Forbidden_regions_cover_head_neck_and_chest_over_heart()
    {
        var backend = NewBackend(out _, out _);

        backend.ForbiddenRegions.Should().BeEquivalentTo(new[]
        {
            BodyMap.BodyRegion.Head,
            BodyMap.BodyRegion.Face,
            BodyMap.BodyRegion.Throat,
            BodyMap.BodyRegion.Neck,
            BodyMap.BodyRegion.ChestOverHeart,
        });
    }

    [Fact]
    public async Task ConnectAsync_initializes_sdk_and_sets_Ready_when_device_present()
    {
        var backend = NewBackend(out var time, out var sdk);
        sdk.InitializeAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await backend.ConnectAsync(CancellationToken.None);

        await sdk.Received(1).InitializeAsync(Arg.Any<CancellationToken>());
        backend.Status.Should().Be(BackendStatus.Ready);
        backend.Calibration.Should().NotBeNull();
        backend.Calibration!.Calibrated.Should().BeTrue();
        backend.Calibration.LastCalibratedAt.ToDateTimeOffset().Should().Be(time.GetUtcNow());
    }

    [Fact]
    public async Task ConnectAsync_stays_Disconnected_when_device_not_present()
    {
        var backend = NewBackend(out _, out var sdk, deviceConnected: false);
        sdk.InitializeAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await backend.ConnectAsync(CancellationToken.None);

        backend.Status.Should().Be(BackendStatus.Disconnected,
            "missing device is not a fatal error — the heartbeat will flip Ready when it shows up");
        backend.Calibration.Should().BeNull();
    }

    [Fact]
    public async Task ConnectAsync_sets_Error_when_sdk_init_throws()
    {
        var backend = NewBackend(out _, out var sdk);
        sdk.InitializeAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("Player unavailable")));

        var act = async () => await backend.ConnectAsync(CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();

        backend.Status.Should().Be(BackendStatus.Error);
    }

    [Fact]
    public async Task TriggerAsync_throws_when_not_ready()
    {
        var backend = NewBackend(out _, out _);

        var act = async () => await backend.TriggerAsync(
            MakeRequest("s1", TimeSpan.FromSeconds(1)), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Disconnected*cannot trigger*");
    }

    [Fact]
    public async Task TriggerAsync_emits_Started_then_Completed_after_duration()
    {
        var (backend, sdk, time) = await NewReadyBackend();
        await using var ____ = backend;

        var result = await backend.TriggerAsync(
            MakeRequest("s1", TimeSpan.FromSeconds(2)), CancellationToken.None);
        result.EstimatedDuration.Should().Be(TimeSpan.FromSeconds(2));

        var enumerator = backend.Events.GetAsyncEnumerator();

        (await NextWithin(enumerator, TimeSpan.FromSeconds(1)))
            .Should().BeOfType<SensationStarted>();

        time.Advance(TimeSpan.FromSeconds(2));

        (await NextWithin(enumerator, TimeSpan.FromSeconds(1)))
            .Should().BeOfType<SensationCompleted>();
    }

    [Fact]
    public async Task TriggerAsync_submits_motor_payload_with_zone_intensity()
    {
        var (backend, sdk, _) = await NewReadyBackend();
        await using var ____ = backend;

        var values = new Dictionary<string, ParameterValue>
        {
            ["intensity"] = new ParameterValue.Number(60),
            ["duration"] = new ParameterValue.Duration(TimeSpan.FromMilliseconds(100)),
        };
        var request = new BackendTriggerRequest(
            SensationId: "s1",
            SensationName: "test",
            ZoneIds: new[] { "pectoral_l" },
            IntensityScale: null,
            Priority: 0,
            ClientTraceId: "trace",
            Microsensations: new[] { new MicrosensationParameters(values) });

        await backend.TriggerAsync(request, CancellationToken.None);
        await Task.Delay(50);

        sdk.Received(1).Submit(
            "vest",
            Arg.Is<byte[]>(p =>
                p.Length == 40
                // pectoral_l covers motors 0, 1, 4, 5 at intensity 60
                && p[0] == 60 && p[1] == 60 && p[4] == 60 && p[5] == 60
                // every other motor stays 0
                && p[2] == 0 && p[3] == 0 && p[6] == 0 && p[10] == 0 && p[39] == 0),
            100);
    }

    [Fact]
    public async Task TriggerAsync_applies_intensity_scale()
    {
        var (backend, sdk, _) = await NewReadyBackend();
        await using var ____ = backend;

        var values = new Dictionary<string, ParameterValue>
        {
            ["intensity"] = new ParameterValue.Number(80),
            ["duration"] = new ParameterValue.Duration(TimeSpan.FromMilliseconds(50)),
        };
        var request = new BackendTriggerRequest(
            SensationId: "scaled",
            SensationName: "test",
            ZoneIds: new[] { "pectoral_l" },
            IntensityScale: 50, // half
            Priority: 0,
            ClientTraceId: "trace",
            Microsensations: new[] { new MicrosensationParameters(values) });

        await backend.TriggerAsync(request, CancellationToken.None);
        await Task.Delay(50);

        sdk.Received(1).Submit(
            "vest",
            Arg.Is<byte[]>(p => p[0] == 40 && p[1] == 40),
            50);
    }

    [Fact]
    public async Task TriggerAsync_takes_max_intensity_across_overlapping_zones()
    {
        var (backend, sdk, _) = await NewReadyBackend();
        await using var ____ = backend;

        // Two microsensations targeting overlapping motor sets. Per microsensation,
        // each zone's motors are written at that microsensation's intensity.
        // Within one microsensation, if multiple zones land on the same motor,
        // the max is taken (closer to user intent than sum-and-clamp).
        //
        // Fire one microsensation with two zones that share no motors but both
        // hit the pectoral_l device-specific zone:
        var values = new Dictionary<string, ParameterValue>
        {
            ["intensity"] = new ParameterValue.Number(50),
            ["duration"] = new ParameterValue.Duration(TimeSpan.FromMilliseconds(100)),
        };
        var request = new BackendTriggerRequest(
            SensationId: "overlap",
            SensationName: "test",
            // pectoral_l covers 0,1,4,5; bhaptics_vest_chest_high_l covers 0,1.
            ZoneIds: new[] { "pectoral_l", "bhaptics_vest_chest_high_l" },
            IntensityScale: null,
            Priority: 0,
            ClientTraceId: "trace",
            Microsensations: new[] { new MicrosensationParameters(values) });

        await backend.TriggerAsync(request, CancellationToken.None);
        await Task.Delay(50);

        // Motors 0 and 1 hit by both zones → max of 50 and 50 = 50 (single
        // intensity, so the max == the value).
        sdk.Received(1).Submit(
            "vest",
            Arg.Is<byte[]>(p =>
                p[0] == 50 && p[1] == 50 && p[4] == 50 && p[5] == 50
                && p[2] == 0),
            100);
    }

    [Fact]
    public async Task TriggerAsync_expands_torso_group_to_member_leaves()
    {
        var (backend, sdk, _) = await NewReadyBackend();
        await using var ____ = backend;

        var values = new Dictionary<string, ParameterValue>
        {
            ["intensity"] = new ParameterValue.Number(50),
            ["duration"] = new ParameterValue.Duration(TimeSpan.FromMilliseconds(100)),
        };
        var request = new BackendTriggerRequest(
            SensationId: "torso",
            SensationName: "test",
            ZoneIds: new[] { "torso" },
            IntensityScale: null,
            Priority: 0,
            ClientTraceId: "trace",
            Microsensations: new[] { new MicrosensationParameters(values) });

        await backend.TriggerAsync(request, CancellationToken.None);
        await Task.Delay(50);

        // torso expands to all 8 quadrant zones, covering every motor 0..39.
        sdk.Received(1).Submit(
            "vest",
            Arg.Is<byte[]>(p => p.All(b => b == 50)),
            100);
    }

    [Fact]
    public async Task StopAsync_cancels_active_sensation_calls_sdk_StopDevice_and_emits_Cancelled()
    {
        var (backend, sdk, _) = await NewReadyBackend();
        await using var ____ = backend;

        await backend.TriggerAsync(
            MakeRequest("s1", TimeSpan.FromSeconds(5)), CancellationToken.None);
        await Task.Delay(50);

        var stopped = await backend.StopAsync(
            new BackendStopRequest("s1", All: false), CancellationToken.None);

        stopped.Should().Be(1);
        sdk.Received().StopDevice("vest");
    }

    [Fact]
    public async Task StopAsync_with_an_in_flight_playback_calls_sdk_StopDevice_exactly_once()
    {
        var (backend, sdk, _) = await NewReadyBackend();
        await using var ____ = backend;

        await backend.TriggerAsync(MakeRequest("a", TimeSpan.FromSeconds(5)), CancellationToken.None);
        await Task.Delay(50);

        await backend.StopAsync(new BackendStopRequest(SensationId: null, All: true), CancellationToken.None);
        await Task.Delay(150);

        sdk.Received(1).Submit("vest", Arg.Any<byte[]>(), Arg.Any<int>());
        sdk.Received(1).StopDevice("vest");
    }

    [Fact]
    public async Task DisposeAsync_does_not_dispose_shared_sdk()
    {
        var (backend, sdk, _) = await NewReadyBackend();

        await backend.DisposeAsync();

        // The SDK is a daemon-wide singleton shared across every bhaptics_*
        // backend; disposing it would tear down the WebSocket for other
        // backends still in use. The base class's DisposeAsync calls
        // StopDevice but never DisposeAsync on the SDK.
        await sdk.DidNotReceive().DisposeAsync();
    }

    [Fact]
    public void StopDeviceIfStillLatest_returns_true_and_calls_StopDevice_when_sequence_is_latest()
    {
        var backend = NewBackend(out _, out var sdk);

        var seq = backend.SubmitAndStamp(new byte[40], 100, CancellationToken.None);

        backend.StopDeviceIfStillLatest(seq).Should().BeTrue();
        sdk.Received(1).StopDevice("vest");
    }

    [Fact]
    public void StopDeviceIfStillLatest_returns_false_when_superseded()
    {
        var backend = NewBackend(out _, out var sdk);

        var seqA = backend.SubmitAndStamp(new byte[40], 100, CancellationToken.None);
        backend.SubmitAndStamp(new byte[40], 100, CancellationToken.None); // supersedes

        backend.StopDeviceIfStillLatest(seqA).Should().BeFalse();
        sdk.DidNotReceive().StopDevice("vest");
    }

    [Fact]
    public void SubmitAndStamp_throws_when_token_already_cancelled()
    {
        var backend = NewBackend(out _, out var sdk);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => backend.SubmitAndStamp(new byte[40], 100, cts.Token);

        act.Should().Throw<OperationCanceledException>();
        sdk.DidNotReceive().Submit(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<int>());
    }

    [Fact]
    public async Task Preemption_does_not_silence_replacement()
    {
        // Mirrors the OwoBackend CANCEL_OLDEST preemption regression: the
        // TriggerCoordinator's preempt path cancels the old sensation's
        // CTS directly and dispatches the replacement via TriggerAsync.
        // The old playback's OCE catch must NOT issue StopDevice because
        // the replacement has already taken over.
        var (backend, sdk, _) = await NewReadyBackend();
        await using var ____ = backend;

        using var oldCts = new CancellationTokenSource();
        await backend.TriggerAsync(MakeRequest("old", TimeSpan.FromSeconds(5)), oldCts.Token);
        await Task.Delay(50);

        sdk.Received(1).Submit("vest", Arg.Any<byte[]>(), Arg.Any<int>());
        sdk.DidNotReceive().StopDevice("vest");

        oldCts.Cancel();
        await backend.TriggerAsync(MakeRequest("new", TimeSpan.FromSeconds(5)), CancellationToken.None);
        await Task.Delay(150);

        sdk.Received(2).Submit("vest", Arg.Any<byte[]>(), Arg.Any<int>());
        sdk.DidNotReceive().StopDevice("vest");
    }

    private static BackendTriggerRequest MakeRequest(string id, TimeSpan duration)
    {
        var values = new Dictionary<string, ParameterValue>
        {
            ["intensity"] = new ParameterValue.Number(60),
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

    private static async Task<(BhapticsVestBackend backend, IBhapticsSdk sdk, FakeTimeProvider time)> NewReadyBackend()
    {
        var backend = NewBackend(out var time, out var sdk);
        sdk.InitializeAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        await backend.ConnectAsync(CancellationToken.None);
        return (backend, sdk, time);
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

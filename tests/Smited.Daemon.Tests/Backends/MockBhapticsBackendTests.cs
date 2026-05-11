using System.Collections.Immutable;
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

/// <summary>
/// Cross-platform tests for the mock bHaptics backends. The motor-
/// payload capture (<see cref="IMockBhapticsController.RecentSubmissions"/>)
/// is the load-bearing surface here: tests on Mac/Linux verify the
/// exact byte-array a real backend would have submitted to the
/// bHaptics Player.
/// </summary>
public class MockBhapticsBackendTests
{
    [Fact]
    public void Vest_static_descriptors_match_real_backend_shape()
    {
        var backend = NewVest();

        backend.Id.Should().Be("mock-bhaptics-vest");
        // Advertised Kind matches the REAL backend so sensation files
        // declaring backend_kind=bhaptics_vest bind to either this mock
        // or the real BhapticsVestBackend. (The descriptor discriminator
        // mock_bhaptics_vest is the factory's IBackendFactory.Kind, not
        // this property.)
        backend.Kind.Should().Be("bhaptics_vest");
        backend.DeviceKey.Should().Be("vest");
        backend.MotorCount.Should().Be(40);
        backend.Status.Should().Be(BackendStatus.Ready);
        backend.Capabilities.Should().Contain("vibrotactile");
        backend.Capabilities.Should().NotContain("ems");
        backend.Concurrency.MaxConcurrent.Should().Be(1u);
        backend.Concurrency.Policy.Should().Be(ConcurrencyPolicy.CancelOldest);
        // Forbidden regions copied from the real vest backend's set.
        backend.ForbiddenRegions.Should().HaveCount(5);
    }

    [Fact]
    public void Parameter_schema_omits_frequency()
    {
        var backend = NewVest();
        var names = backend.Parameters.Parameters.Select(p => p.Name).ToList();
        names.Should().BeEquivalentTo(
            "intensity", "duration", "ramp_up", "ramp_down", "exit_delay");
    }

    [Fact]
    public void BuildDiagnosticMicrosensation_satisfies_every_required_parameter_and_omits_frequency()
    {
        var backend = NewVest();
        var diag = backend.BuildDiagnosticMicrosensation();

        var required = backend.Parameters.Parameters.Where(p => p.Required).Select(p => p.Name);
        diag.Values.Keys.Should().Contain(required);

        diag.Values.Should().NotContainKey("frequency",
            "bHaptics is vibrotactile and its schema does not declare a frequency parameter");
    }

    [Fact]
    public async Task TriggerAsync_captures_motor_payload_for_pectoral_zone()
    {
        var backend = NewVest();
        backend.ClearSubmissions();

        var request = MakeRequest("s1", "pectoral_l", intensity: 60, durationMs: 100);
        await backend.TriggerAsync(request, CancellationToken.None);

        var submissions = backend.RecentSubmissions;
        submissions.Should().HaveCount(1);
        var sub = submissions[0];
        sub.DeviceKey.Should().Be("vest");
        sub.MotorIntensities.Length.Should().Be(40);
        // pectoral_l covers motors 0, 1, 4, 5 at intensity 60.
        sub.MotorIntensities[0].Should().Be(60);
        sub.MotorIntensities[1].Should().Be(60);
        sub.MotorIntensities[4].Should().Be(60);
        sub.MotorIntensities[5].Should().Be(60);
        sub.MotorIntensities[2].Should().Be(0);
        sub.MotorIntensities[10].Should().Be(0);
        sub.Duration.Should().Be(TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public async Task TriggerAsync_applies_intensity_scale_to_motor_payload()
    {
        var backend = NewVest();
        backend.ClearSubmissions();

        var values = new Dictionary<string, ParameterValue>
        {
            ["intensity"] = new ParameterValue.Number(80),
            ["duration"] = new ParameterValue.Duration(TimeSpan.FromMilliseconds(50)),
        };
        var request = new BackendTriggerRequest(
            SensationId: "scaled",
            SensationName: null,
            ZoneIds: new[] { "pectoral_l" },
            IntensityScale: 50,
            Priority: 0,
            ClientTraceId: "trace",
            Microsensations: new[] { new MicrosensationParameters(values) });

        await backend.TriggerAsync(request, CancellationToken.None);

        var sub = backend.RecentSubmissions.Should().ContainSingle().Subject;
        sub.MotorIntensities[0].Should().Be(40,
            "intensity 80 * scale 50 / 100 = 40");
    }

    [Fact]
    public async Task TriggerAsync_emits_Started_then_Completed_under_FakeTime()
    {
        var backend = NewVest(out var time);

        await backend.TriggerAsync(
            MakeRequest("s1", "pectoral_l", intensity: 50, durationMs: 200),
            CancellationToken.None);

        var enumerator = backend.Events.GetAsyncEnumerator();
        (await NextWithin(enumerator, TimeSpan.FromSeconds(1)))
            .Should().BeOfType<SensationStarted>();

        time.Advance(TimeSpan.FromMilliseconds(200));

        (await NextWithin(enumerator, TimeSpan.FromSeconds(1)))
            .Should().BeOfType<SensationCompleted>();
    }

    [Fact]
    public async Task ClearSubmissions_resets_buffer_between_tests()
    {
        var backend = NewVest();

        await backend.TriggerAsync(MakeRequest("a", "pectoral_l", 50, 50), CancellationToken.None);
        backend.RecentSubmissions.Should().HaveCount(1);

        backend.ClearSubmissions();
        backend.RecentSubmissions.Should().BeEmpty();

        await backend.TriggerAsync(MakeRequest("b", "abdominal_l", 50, 50), CancellationToken.None);
        backend.RecentSubmissions.Should().HaveCount(1);
    }

    [Fact]
    public async Task Recent_submissions_buffer_caps_at_100_drop_oldest()
    {
        var backend = NewVest();
        backend.ClearSubmissions();

        for (var i = 0; i < 120; i++)
        {
            await backend.TriggerAsync(
                MakeRequest($"s{i}", "pectoral_l", 50, 1), CancellationToken.None);
        }

        backend.RecentSubmissions.Should().HaveCount(100,
            "the cap is 100 with drop-oldest, so the last 100 of 120 submissions survive");
    }

    [Theory]
    [InlineData("left", "bhaptics_sleeve_l", "sleeve_l", 6)]
    [InlineData("right", "bhaptics_sleeve_r", "sleeve_r", 6)]
    public void Sleeve_descriptors_match_per_side(string side, string kind, string deviceKey, int motors)
    {
        var backend = NewSleeve(side);
        backend.Kind.Should().Be(kind);
        backend.DeviceKey.Should().Be(deviceKey);
        backend.MotorCount.Should().Be(motors);
    }

    [Theory]
    [InlineData("left", "bhaptics_feet_l", "feet_l", 3)]
    [InlineData("right", "bhaptics_feet_r", "feet_r", 3)]
    public void Feet_descriptors_match_per_side(string side, string kind, string deviceKey, int motors)
    {
        var backend = NewFeet(side);
        backend.Kind.Should().Be(kind);
        backend.DeviceKey.Should().Be(deviceKey);
        backend.MotorCount.Should().Be(motors);
    }

    [Fact]
    public async Task Sleeve_left_captures_correct_motor_payload_for_wrist_zone()
    {
        var backend = NewSleeve("left");
        backend.ClearSubmissions();

        var request = MakeRequest("s1", "bhaptics_sleeve_wrist_l", intensity: 70, durationMs: 50);
        await backend.TriggerAsync(request, CancellationToken.None);

        var sub = backend.RecentSubmissions.Should().ContainSingle().Subject;
        sub.DeviceKey.Should().Be("sleeve_l");
        sub.MotorIntensities.Length.Should().Be(6);
        sub.MotorIntensities[0].Should().Be(70);
        sub.MotorIntensities[1].Should().Be(0);
        sub.MotorIntensities[5].Should().Be(0);
    }

    [Fact]
    public async Task Feet_right_heel_zone_pulses_motor_zero_only()
    {
        var backend = NewFeet("right");
        backend.ClearSubmissions();

        var request = MakeRequest("s1", "bhaptics_feet_heel_r", intensity: 80, durationMs: 60);
        await backend.TriggerAsync(request, CancellationToken.None);

        var sub = backend.RecentSubmissions.Should().ContainSingle().Subject;
        sub.DeviceKey.Should().Be("feet_r");
        sub.MotorIntensities.Should().Equal((byte)80, (byte)0, (byte)0);
    }

    [Fact]
    public void Mock_submission_byte_array_is_immutable()
    {
        // ImmutableArray<byte> can't be mutated through the captured
        // submission; the cast back from a default value is verifiable
        // at the type level. This test is mostly a contract check
        // against a future refactor that swaps the type back to byte[].
        var sub = new MockBhapticsSubmission(
            "vest",
            new byte[] { 1, 2, 3 }.ToImmutableArray(),
            TimeSpan.FromMilliseconds(50),
            DateTimeOffset.UtcNow);

        // Compile-time: there's no public setter; runtime: structural
        // equality holds across new captures of identical arrays.
        sub.MotorIntensities.Should().Equal((byte)1, (byte)2, (byte)3);
    }

    private static MockBhapticsVestBackend NewVest() => NewVest(out _);

    private static MockBhapticsVestBackend NewVest(out FakeTimeProvider time)
    {
        time = new FakeTimeProvider(new DateTimeOffset(2026, 5, 9, 12, 0, 0, TimeSpan.Zero));
        return new MockBhapticsVestBackend(time, NullLogger<MockBhapticsVestBackend>.Instance);
    }

    private static MockBhapticsSleeveBackend NewSleeve(string side)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 5, 9, 12, 0, 0, TimeSpan.Zero));
        return new MockBhapticsSleeveBackend(side, time, NullLogger<MockBhapticsSleeveBackend>.Instance);
    }

    private static MockBhapticsFeetBackend NewFeet(string side)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 5, 9, 12, 0, 0, TimeSpan.Zero));
        return new MockBhapticsFeetBackend(side, time, NullLogger<MockBhapticsFeetBackend>.Instance);
    }

    private static BackendTriggerRequest MakeRequest(string id, string zoneId, double intensity, int durationMs)
    {
        var values = new Dictionary<string, ParameterValue>
        {
            ["intensity"] = new ParameterValue.Number(intensity),
            ["duration"] = new ParameterValue.Duration(TimeSpan.FromMilliseconds(durationMs)),
        };
        return new BackendTriggerRequest(
            SensationId: id,
            SensationName: null,
            ZoneIds: new[] { zoneId },
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
        if (winner != task) throw new TimeoutException($"No event in {timeout}");
        var ok = await task;
        if (!ok) throw new InvalidOperationException("Stream completed unexpectedly");
        return enumerator.Current;
    }
}

// Excluded from compile on non-Windows hosts via the test csproj's
// conditional <Compile Remove>. References BhapticsFeetBackend.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Smited.Daemon.Backends;
using Smited.Daemon.Backends.Internal;
using Smited.Daemon.Bhaptics;
using Xunit;
using ParameterValue = Smited.Daemon.Backends.Internal.ParameterValue;
using MicrosensationParameters = Smited.Daemon.Backends.Internal.MicrosensationParameters;

namespace Smited.Daemon.Tests.Backends;

public class BhapticsFeetBackendTests
{
    [Theory]
    [InlineData("left", "bhaptics_feet_l", "feet_l", "foot_l")]
    [InlineData("right", "bhaptics_feet_r", "feet_r", "foot_r")]
    public void Side_determines_kind_device_key_and_zone_topology(
        string side, string expectedKind, string expectedDeviceKey, string expectedPortableZone)
    {
        var backend = NewBackend(side, out _, out _);

        backend.Kind.Should().Be(expectedKind);
        backend.DeviceKey.Should().Be(expectedDeviceKey);
        backend.MotorCount.Should().Be(3);
        backend.Zones.Zones.Select(z => z.Id).Should().Contain(expectedPortableZone);
    }

    [Fact]
    public void Constructor_rejects_invalid_side()
    {
        var act = () => NewBackend("middle", out _, out _);
        act.Should().Throw<ArgumentException>().WithMessage("*'left' or 'right'*");
    }

    [Fact]
    public async Task TriggerAsync_heel_only_zone_pulses_motor_zero_only()
    {
        var backend = NewBackend("left", out _, out var sdk);
        sdk.InitializeAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        sdk.IsDeviceConnected("feet_l").Returns(true);
        await backend.ConnectAsync(CancellationToken.None);
        await using var ____ = backend;

        var values = new Dictionary<string, ParameterValue>
        {
            ["intensity"] = new ParameterValue.Number(80),
            ["duration"] = new ParameterValue.Duration(TimeSpan.FromMilliseconds(75)),
        };
        var request = new BackendTriggerRequest(
            SensationId: "heel-only",
            SensationName: "test",
            ZoneIds: new[] { "bhaptics_feet_heel_l" },
            IntensityScale: null,
            Priority: 0,
            ClientTraceId: "trace",
            Microsensations: new[] { new MicrosensationParameters(values) });

        await backend.TriggerAsync(request, CancellationToken.None);
        await Task.Delay(50);

        sdk.Received(1).Submit(
            "feet_l",
            Arg.Is<byte[]>(p => p.Length == 3 && p[0] == 80 && p[1] == 0 && p[2] == 0),
            75);
    }

    [Fact]
    public async Task TriggerAsync_whole_foot_zone_pulses_all_three_motors()
    {
        var backend = NewBackend("right", out _, out var sdk);
        sdk.InitializeAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        sdk.IsDeviceConnected("feet_r").Returns(true);
        await backend.ConnectAsync(CancellationToken.None);
        await using var ____ = backend;

        var values = new Dictionary<string, ParameterValue>
        {
            ["intensity"] = new ParameterValue.Number(40),
            ["duration"] = new ParameterValue.Duration(TimeSpan.FromMilliseconds(120)),
        };
        var request = new BackendTriggerRequest(
            SensationId: "pulse",
            SensationName: "test",
            ZoneIds: new[] { "foot_r" },
            IntensityScale: null,
            Priority: 0,
            ClientTraceId: "trace",
            Microsensations: new[] { new MicrosensationParameters(values) });

        await backend.TriggerAsync(request, CancellationToken.None);
        await Task.Delay(50);

        sdk.Received(1).Submit(
            "feet_r",
            Arg.Is<byte[]>(p => p.Length == 3 && p.All(b => b == 40)),
            120);
    }

    private static BhapticsFeetBackend NewBackend(
        string side, out FakeTimeProvider time, out IBhapticsSdk sdk)
    {
        time = new FakeTimeProvider(new DateTimeOffset(2026, 5, 9, 12, 0, 0, TimeSpan.Zero));
        sdk = Substitute.For<IBhapticsSdk>();
        return new BhapticsFeetBackend(
            side,
            new BhapticsFeetOptions(),
            sdk,
            time,
            NullLogger<BhapticsFeetBackend>.Instance);
    }
}

// Excluded from compile on non-Windows hosts via the test csproj's
// conditional <Compile Remove>. References BhapticsSleeveBackend.

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

public class BhapticsSleeveBackendTests
{
    [Theory]
    [InlineData("left", "bhaptics_sleeve_l", "sleeve_l", "arm_l")]
    [InlineData("right", "bhaptics_sleeve_r", "sleeve_r", "arm_r")]
    public void Side_determines_kind_device_key_and_zone_topology(
        string side, string expectedKind, string expectedDeviceKey, string expectedPortableZone)
    {
        var backend = NewBackend(side, out _, out _);

        backend.Kind.Should().Be(expectedKind);
        backend.DeviceKey.Should().Be(expectedDeviceKey);
        backend.MotorCount.Should().Be(6);
        backend.Zones.Zones.Select(z => z.Id).Should().Contain(expectedPortableZone);
    }

    [Fact]
    public void Constructor_rejects_invalid_side()
    {
        var act = () => NewBackend("middle", out _, out _);
        act.Should().Throw<ArgumentException>().WithMessage("*'left' or 'right'*");
    }

    [Fact]
    public void Forbidden_regions_are_empty()
    {
        var backend = NewBackend("left", out _, out _);
        backend.ForbiddenRegions.Should().BeEmpty(
            "arms have no manufacturer-mandated vibrotactile safety bans");
    }

    [Fact]
    public async Task TriggerAsync_left_sleeve_submits_to_sleeve_l_device()
    {
        var backend = NewBackend("left", out var time, out var sdk);
        sdk.InitializeAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        sdk.IsDeviceConnected("sleeve_l").Returns(true);
        await backend.ConnectAsync(CancellationToken.None);
        await using var ____ = backend;

        var values = new Dictionary<string, ParameterValue>
        {
            ["intensity"] = new ParameterValue.Number(50),
            ["duration"] = new ParameterValue.Duration(TimeSpan.FromMilliseconds(100)),
        };
        var request = new BackendTriggerRequest(
            SensationId: "s1",
            SensationName: "test",
            ZoneIds: new[] { "arm_l" },
            IntensityScale: null,
            Priority: 0,
            ClientTraceId: "trace",
            Microsensations: new[] { new MicrosensationParameters(values) });

        await backend.TriggerAsync(request, CancellationToken.None);
        await Task.Delay(50);

        // arm_l covers all six sleeve motors at intensity 50.
        sdk.Received(1).Submit(
            "sleeve_l",
            Arg.Is<byte[]>(p => p.Length == 6 && p.All(b => b == 50)),
            100);
    }

    [Fact]
    public async Task TriggerAsync_right_sleeve_submits_to_sleeve_r_device()
    {
        var backend = NewBackend("right", out _, out var sdk);
        sdk.InitializeAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        sdk.IsDeviceConnected("sleeve_r").Returns(true);
        await backend.ConnectAsync(CancellationToken.None);
        await using var ____ = backend;

        var values = new Dictionary<string, ParameterValue>
        {
            ["intensity"] = new ParameterValue.Number(70),
            ["duration"] = new ParameterValue.Duration(TimeSpan.FromMilliseconds(50)),
        };
        var request = new BackendTriggerRequest(
            SensationId: "s1",
            SensationName: "test",
            ZoneIds: new[] { "bhaptics_sleeve_wrist_r" },
            IntensityScale: null,
            Priority: 0,
            ClientTraceId: "trace",
            Microsensations: new[] { new MicrosensationParameters(values) });

        await backend.TriggerAsync(request, CancellationToken.None);
        await Task.Delay(50);

        // wrist on the right sleeve covers motor 0 only.
        sdk.Received(1).Submit(
            "sleeve_r",
            Arg.Is<byte[]>(p =>
                p.Length == 6
                && p[0] == 70
                && p[1] == 0 && p[2] == 0 && p[3] == 0 && p[4] == 0 && p[5] == 0),
            50);
    }

    [Fact]
    public async Task Left_sleeve_ignores_right_zone_id()
    {
        var backend = NewBackend("left", out _, out var sdk);
        sdk.InitializeAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        sdk.IsDeviceConnected("sleeve_l").Returns(true);
        await backend.ConnectAsync(CancellationToken.None);
        await using var ____ = backend;

        var values = new Dictionary<string, ParameterValue>
        {
            ["intensity"] = new ParameterValue.Number(50),
            ["duration"] = new ParameterValue.Duration(TimeSpan.FromMilliseconds(100)),
        };
        // arm_r is a right-sleeve zone; the left-sleeve backend's motor map
        // returns Array.Empty<int>() for it, so the payload stays all-zero.
        var request = new BackendTriggerRequest(
            SensationId: "wrong-side",
            SensationName: "test",
            ZoneIds: new[] { "arm_r" },
            IntensityScale: null,
            Priority: 0,
            ClientTraceId: "trace",
            Microsensations: new[] { new MicrosensationParameters(values) });

        await backend.TriggerAsync(request, CancellationToken.None);
        await Task.Delay(50);

        sdk.Received(1).Submit(
            "sleeve_l",
            Arg.Is<byte[]>(p => p.Length == 6 && p.All(b => b == 0)),
            100);
    }

    private static BhapticsSleeveBackend NewBackend(
        string side, out FakeTimeProvider time, out IBhapticsSdk sdk)
    {
        time = new FakeTimeProvider(new DateTimeOffset(2026, 5, 9, 12, 0, 0, TimeSpan.Zero));
        sdk = Substitute.For<IBhapticsSdk>();
        return new BhapticsSleeveBackend(
            side,
            new BhapticsSleeveOptions(),
            sdk,
            time,
            NullLogger<BhapticsSleeveBackend>.Instance);
    }
}

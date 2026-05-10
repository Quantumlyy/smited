using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Smited.Daemon.Backends;
using Smited.Daemon.Backends.Internal;
using Smited.Daemon.Bhaptics;
using Smited.Daemon.Bhaptics.WebSocket;
using Smited.Daemon.Tests.Bhaptics.Fixtures;
using Smited.V1;
using Xunit;

namespace Smited.Daemon.Tests.Bhaptics;

/// <summary>
/// Integration coverage for the real <see cref="BhapticsBackend"/>. The
/// backend points at a Kestrel-backed <see cref="BhapticsPlayerSimulator"/>
/// instead of a real bHaptics Player, so the round-trip exercises the
/// daemon's own logic without needing hardware. The on-skin smoke test
/// checklist in <c>docs/bhaptics.md</c> covers what these tests can't.
/// </summary>
public class BhapticsBackendTests
{
    [Fact]
    public async Task ConnectAsync_advertises_vest_only_topology_initially()
    {
        await using var sim = await BhapticsPlayerSimulator.StartAsync();
        await using var backend = NewBackend(sim.Endpoint);

        await backend.ConnectAsync(CancellationToken.None);

        backend.Zones.Zones.Should().HaveCount(40);
        backend.Zones.Zones.Select(z => z.Id).Should()
            .NotContain(id => id.StartsWith("arm_"))
            .And.NotContain(id => id.StartsWith("glove_"));
    }

    [Fact]
    public async Task DeviceStatus_with_TactSleeve_connected_expands_topology()
    {
        await using var sim = await BhapticsPlayerSimulator.StartAsync();
        await using var backend = NewBackend(sim.Endpoint);
        await backend.ConnectAsync(CancellationToken.None);

        var enumerator = backend.Events.GetAsyncEnumerator();

        await sim.PushAsync(new
        {
            type = "deviceStatus",
            devices = new[]
            {
                new { position = (int)Position.Vest, connected = true, batteryPercent = 87 },
                new { position = (int)Position.ForearmL, connected = true, batteryPercent = 65 },
                new { position = (int)Position.ForearmR, connected = true, batteryPercent = 70 },
            },
        });

        var evt = await NextWithin(enumerator, TimeSpan.FromSeconds(2));
        var lifecycle = evt.Should().BeOfType<BackendLifecycleEvent>().Subject;
        lifecycle.Change.Should().Be(BackendLifecycleChange.StatusChanged);
        lifecycle.Reason.Should().Be("accessories_present");

        backend.Zones.Zones.Should().HaveCount(60);
        backend.Zones.Zones.Select(z => z.Id).Should()
            .Contain("arm_l_0").And.Contain("arm_r_3");
        backend.Zones.Groups.Select(g => g.Id).Should().Contain("arms");
        backend.Zones.Groups.Single(g => g.Id == "all").ZoneIds.Should().HaveCount(60);
    }

    [Fact]
    public async Task DeviceStatus_heartbeat_with_unchanged_set_does_not_re_emit_lifecycle()
    {
        await using var sim = await BhapticsPlayerSimulator.StartAsync();
        await using var backend = NewBackend(sim.Endpoint);
        await backend.ConnectAsync(CancellationToken.None);

        var enumerator = backend.Events.GetAsyncEnumerator();

        var statusFrame = new
        {
            type = "deviceStatus",
            devices = new[]
            {
                new { position = (int)Position.Vest, connected = true, batteryPercent = 87 },
                new { position = (int)Position.ForearmL, connected = true, batteryPercent = 65 },
            },
        };

        await sim.PushAsync(statusFrame);
        var first = await NextWithin(enumerator, TimeSpan.FromSeconds(2));
        first.Should().BeOfType<BackendLifecycleEvent>();

        // Same membership: no second event.
        await sim.PushAsync(statusFrame);

        var stillNothing = await NextOrNullWithin(enumerator, TimeSpan.FromMilliseconds(300));
        stillNothing.Should().BeNull("heartbeat frames with unchanged accessory membership shouldn't re-emit StatusChanged");
    }

    [Fact]
    public async Task DeviceStatus_with_sleeve_disconnect_collapses_topology_back_to_vest()
    {
        await using var sim = await BhapticsPlayerSimulator.StartAsync();
        await using var backend = NewBackend(sim.Endpoint);
        await backend.ConnectAsync(CancellationToken.None);

        var enumerator = backend.Events.GetAsyncEnumerator();

        await sim.PushAsync(new
        {
            type = "deviceStatus",
            devices = new[]
            {
                new { position = (int)Position.Vest, connected = true, batteryPercent = 87 },
                new { position = (int)Position.ForearmL, connected = true, batteryPercent = 65 },
            },
        });
        var expansion = await NextWithin(enumerator, TimeSpan.FromSeconds(2));
        expansion.Should().BeOfType<BackendLifecycleEvent>()
            .Which.Reason.Should().Be("accessories_present");

        await sim.PushAsync(new
        {
            type = "deviceStatus",
            devices = new[]
            {
                new { position = (int)Position.Vest, connected = true, batteryPercent = 86 },
                new { position = (int)Position.ForearmL, connected = false, batteryPercent = 0 },
            },
        });
        var collapse = await NextWithin(enumerator, TimeSpan.FromSeconds(2));
        collapse.Should().BeOfType<BackendLifecycleEvent>()
            .Which.Reason.Should().Be("accessories_absent");

        backend.Zones.Zones.Should().HaveCount(40);
    }

    private static BhapticsBackend NewBackend(Uri endpoint)
    {
        var options = new BhapticsBackendOptions
        {
            BackendId = "bhaptics-test",
            PlayerEndpoint = endpoint.ToString(),
            MaxReconnectAttempts = 0,
        };
        return new BhapticsBackend(options, NullLogger<BhapticsBackend>.Instance, new FakeTimeProvider());
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

    private static async Task<BackendEvent?> NextOrNullWithin(
        IAsyncEnumerator<BackendEvent> enumerator,
        TimeSpan timeout)
    {
        var task = enumerator.MoveNextAsync().AsTask();
        var winner = await Task.WhenAny(task, Task.Delay(timeout));
        if (winner != task) return null;
        var ok = await task;
        if (!ok) return null;
        return enumerator.Current;
    }
}

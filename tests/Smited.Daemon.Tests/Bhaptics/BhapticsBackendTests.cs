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
using ParameterValue = Smited.Daemon.Backends.Internal.ParameterValue;
using MicrosensationParameters = Smited.Daemon.Backends.Internal.MicrosensationParameters;

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

    [Fact]
    public async Task Trigger_with_group_zone_id_expands_to_constituent_motor_indices()
    {
        await using var sim = await BhapticsPlayerSimulator.StartAsync();
        await using var backend = NewBackend(sim.Endpoint);
        await backend.ConnectAsync(CancellationToken.None);

        var request = MakeRequest(new[] { "front_chest" }, intensity: 60, durationMs: 400);
        await backend.TriggerAsync(request, CancellationToken.None);

        var frame = await ReceiveWithin(sim.ReceivedFrames, TimeSpan.FromSeconds(2));
        var entry = frame.GetProperty("submit")[0];
        entry.GetProperty("frame").GetProperty("position").GetInt32().Should().Be((int)Position.VestFront);

        var dots = entry.GetProperty("frame").GetProperty("dotPoints");
        dots.GetArrayLength().Should().Be(8, "front_chest expands to vest_front_0..7");
        var indices = Enumerable.Range(0, dots.GetArrayLength())
            .Select(i => dots[i].GetProperty("index").GetInt32())
            .OrderBy(i => i)
            .ToArray();
        indices.Should().Equal(0, 1, 2, 3, 4, 5, 6, 7);
        Enumerable.Range(0, dots.GetArrayLength())
            .Select(i => dots[i].GetProperty("intensity").GetInt32())
            .Should().AllSatisfy(v => v.Should().Be(60));
    }

    [Fact]
    public async Task Trigger_with_torso_group_submits_one_frame_per_half()
    {
        await using var sim = await BhapticsPlayerSimulator.StartAsync();
        await using var backend = NewBackend(sim.Endpoint);
        await backend.ConnectAsync(CancellationToken.None);

        var request = MakeRequest(new[] { "torso" }, intensity: 75, durationMs: 200);
        await backend.TriggerAsync(request, CancellationToken.None);

        var frameA = await ReceiveWithin(sim.ReceivedFrames, TimeSpan.FromSeconds(2));
        var frameB = await ReceiveWithin(sim.ReceivedFrames, TimeSpan.FromSeconds(2));

        var positions = new[] { frameA, frameB }
            .Select(f => f.GetProperty("submit")[0].GetProperty("frame").GetProperty("position").GetInt32())
            .OrderBy(p => p)
            .ToArray();
        positions.Should().Equal((int)Position.VestFront, (int)Position.VestBack);

        foreach (var f in new[] { frameA, frameB })
        {
            var dots = f.GetProperty("submit")[0].GetProperty("frame").GetProperty("dotPoints");
            dots.GetArrayLength().Should().Be(20,
                "each half contributes its 20 motors as a separate frame so motor indices don't collide");
        }
    }

    [Fact]
    public async Task Trigger_after_Player_disconnect_throws_synchronously()
    {
        await using var sim = await BhapticsPlayerSimulator.StartAsync();
        await using var backend = NewBackend(sim.Endpoint);
        await backend.ConnectAsync(CancellationToken.None);

        var enumerator = backend.Events.GetAsyncEnumerator();

        await sim.CloseAsync();

        // Wait for OnDisconnected to flip _status before retrying the trigger.
        var lifecycle = await NextWithin(enumerator, TimeSpan.FromSeconds(2));
        lifecycle.Should().BeOfType<BackendLifecycleEvent>()
            .Which.Snapshot.Status.Should().Be(BackendStatus.Disconnected);

        var act = () => backend.TriggerAsync(MakeRequest(new[] { "front_chest" }, 50, 200), CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .Where(ex => ex.Message.Contains("Disconnected"));
    }

    private static BackendTriggerRequest MakeRequest(IReadOnlyList<string> zoneIds, int intensity, int durationMs)
    {
        var micro = new MicrosensationParameters(new Dictionary<string, ParameterValue>
        {
            ["intensity"] = new ParameterValue.Number(intensity),
            ["duration"] = new ParameterValue.Duration(TimeSpan.FromMilliseconds(durationMs)),
        });
        return new BackendTriggerRequest(
            SensationId: Guid.NewGuid().ToString("N")[..16],
            SensationName: "test",
            ZoneIds: zoneIds,
            IntensityScale: null,
            Priority: 0,
            ClientTraceId: "trace",
            Microsensations: new[] { micro });
    }

    private static async Task<System.Text.Json.JsonElement> ReceiveWithin(
        System.Threading.Channels.ChannelReader<System.Text.Json.JsonElement> reader,
        TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            return await reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"No frame received within {timeout}.");
        }
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

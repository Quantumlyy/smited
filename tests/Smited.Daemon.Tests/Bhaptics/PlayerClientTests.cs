using System.Net.Sockets;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Smited.Daemon.Bhaptics.WebSocket;
using Smited.Daemon.Tests.Bhaptics.Fixtures;
using Xunit;

namespace Smited.Daemon.Tests.Bhaptics;

public class PlayerClientTests
{
    [Fact]
    public async Task ConnectAsync_against_reachable_server_completes_without_throw()
    {
        await using var sim = await BhapticsPlayerSimulator.StartAsync();
        await using var client = new PlayerClient(sim.Endpoint, NullLogger<PlayerClient>.Instance);

        await client.ConnectAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ConnectAsync_against_refused_port_throws_with_endpoint_in_message()
    {
        var endpoint = await ReserveClosedEndpointAsync();
        await using var client = new PlayerClient(endpoint, NullLogger<PlayerClient>.Instance);

        var act = () => client.ConnectAsync(CancellationToken.None);

        var ex = await act.Should().ThrowAsync<Exception>();
        ex.Which.Message.Should().Contain(endpoint.Authority);
    }

    [Fact]
    public async Task SubmitDotPatternAsync_sends_frame_matching_v2_schema()
    {
        await using var sim = await BhapticsPlayerSimulator.StartAsync();
        await using var client = new PlayerClient(sim.Endpoint, NullLogger<PlayerClient>.Instance);
        await client.ConnectAsync(CancellationToken.None);

        var key = await client.SubmitDotPatternAsync(
            Position.VestFront,
            new[] { new DotPoint(3, 60), new DotPoint(7, 80) },
            TimeSpan.FromMilliseconds(400),
            CancellationToken.None);

        key.Should().NotBeNullOrEmpty();

        var frame = await ReceiveWithin(sim.ReceivedFrames, TimeSpan.FromSeconds(2));

        frame.GetProperty("type").GetString().Should().Be("frame");
        var submit = frame.GetProperty("submit");
        submit.GetArrayLength().Should().Be(1);
        var entry = submit[0];
        entry.GetProperty("type").GetString().Should().Be("dotMode");
        entry.GetProperty("key").GetString().Should().Be(key);
        entry.GetProperty("durationMillis").GetInt32().Should().Be(400);

        var inner = entry.GetProperty("frame");
        inner.GetProperty("position").GetInt32().Should().Be((int)Position.VestFront);
        inner.GetProperty("durationMillis").GetInt32().Should().Be(400);

        var dots = inner.GetProperty("dotPoints");
        dots.GetArrayLength().Should().Be(2);
        dots[0].GetProperty("index").GetInt32().Should().Be(3);
        dots[0].GetProperty("intensity").GetInt32().Should().Be(60);
        dots[1].GetProperty("index").GetInt32().Should().Be(7);
        dots[1].GetProperty("intensity").GetInt32().Should().Be(80);
    }

    [Fact]
    public async Task Inbound_deviceStatus_frame_fires_DeviceStatusChanged_event()
    {
        await using var sim = await BhapticsPlayerSimulator.StartAsync();
        await using var client = new PlayerClient(sim.Endpoint, NullLogger<PlayerClient>.Instance);
        await client.ConnectAsync(CancellationToken.None);

        var seen = new TaskCompletionSource<IReadOnlyList<DeviceStatus>>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.DeviceStatusChanged += devices => seen.TrySetResult(devices);

        await sim.PushAsync(new
        {
            type = "deviceStatus",
            devices = new[]
            {
                new { position = (int)Position.Vest, connected = true, batteryPercent = 87 },
            },
        });

        var winner = await Task.WhenAny(seen.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        winner.Should().Be(seen.Task, "DeviceStatusChanged should fire within the deadline");

        var devices = await seen.Task;
        devices.Should().ContainSingle();
        devices[0].Position.Should().Be(Position.Vest);
        devices[0].Connected.Should().BeTrue();
        devices[0].BatteryPercent.Should().Be(87);
    }

    [Fact]
    public async Task Connection_drop_fires_Disconnected_event()
    {
        await using var sim = await BhapticsPlayerSimulator.StartAsync();
        await using var client = new PlayerClient(sim.Endpoint, NullLogger<PlayerClient>.Instance);
        await client.ConnectAsync(CancellationToken.None);

        var disconnected = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += ex => disconnected.TrySetResult(ex);

        await sim.CloseAsync();

        var winner = await Task.WhenAny(disconnected.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        winner.Should().Be(disconnected.Task, "Disconnected should fire within the deadline");
    }

    private static async Task<JsonElement> ReceiveWithin(
        System.Threading.Channels.ChannelReader<JsonElement> reader,
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

    /// <summary>
    /// Bind a TcpListener to an OS-assigned port, capture the port, then
    /// release it. The returned URI points at a port we know is closed
    /// — `ConnectAsync` against it should fail fast with connection
    /// refused.
    /// </summary>
    private static Task<Uri> ReserveClosedEndpointAsync()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return Task.FromResult(new Uri($"ws://127.0.0.1:{port}/v2/feedbacks"));
    }
}

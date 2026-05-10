// Excluded from compile on non-Windows hosts via the test csproj's
// conditional <Compile Remove>. References OwoBackend, which lives in
// the Windows-only Smited.Daemon.Owo assembly.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Smited.Daemon.Backends;
using Smited.Daemon.Backends.Internal;
using Smited.Daemon.Owo;
using Smited.V1;
using Xunit;
using ParameterValue = Smited.Daemon.Backends.Internal.ParameterValue;
using MicrosensationParameters = Smited.Daemon.Backends.Internal.MicrosensationParameters;

namespace Smited.Daemon.Tests.Backends;

public class OwoBackendLifecycleTests
{
    private static OwoBackend NewBackend(
        out FakeTimeProvider time,
        out IOwoSdk sdk,
        OwoBackendOptions? options = null)
    {
        time = new FakeTimeProvider(new DateTimeOffset(2026, 5, 9, 12, 0, 0, TimeSpan.Zero));
        sdk = Substitute.For<IOwoSdk>();
        sdk.IsConnected.Returns(true);
        return new OwoBackend(
            options ?? new OwoBackendOptions(),
            sdk,
            time,
            NullLogger<OwoBackend>.Instance);
    }

    [Fact]
    public async Task DisposeAsync_calls_Stop_and_Disconnect_on_the_sdk()
    {
        var backend = NewBackend(out _, out var sdk);
        sdk.AutoConnectAsync().Returns(Task.CompletedTask);
        await backend.ConnectAsync(CancellationToken.None);

        await backend.DisposeAsync();

        sdk.Received().Stop();
        sdk.Received().Disconnect();
    }

    [Fact]
    public async Task DisposeAsync_cancels_in_flight_sensations()
    {
        var backend = NewBackend(out _, out var sdk);
        sdk.AutoConnectAsync().Returns(Task.CompletedTask);
        await backend.ConnectAsync(CancellationToken.None);

        await backend.TriggerAsync(MakeRequest("s1", TimeSpan.FromSeconds(60)), CancellationToken.None);

        var enumerator = backend.Events.GetAsyncEnumerator();
        (await NextWithin(enumerator, TimeSpan.FromSeconds(1)))
            .Should().BeOfType<SensationStarted>();

        await backend.DisposeAsync();

        // After dispose the channel is completed; Cancelled may have been
        // emitted before completion. Drain whatever remains.
        var remaining = new List<BackendEvent>();
        try
        {
            while (await enumerator.MoveNextAsync())
            {
                remaining.Add(enumerator.Current);
            }
        }
        catch (OperationCanceledException) { }

        remaining.OfType<SensationCancelled>().Should().NotBeEmpty(
            "DisposeAsync cancels every active sensation");
    }

    [Fact]
    public async Task DisposeAsync_completes_the_event_channel()
    {
        var backend = NewBackend(out _, out var sdk);
        sdk.AutoConnectAsync().Returns(Task.CompletedTask);
        await backend.ConnectAsync(CancellationToken.None);

        await backend.DisposeAsync();

        var enumerator = backend.Events.GetAsyncEnumerator();
        // Drain anything emitted prior to completion (lifecycle events from
        // Connect aren't emitted by the backend itself, but pump anyway).
        while (await enumerator.MoveNextAsync()) { }
        // Reaching here without hanging means the writer is completed.
    }

    [Fact]
    public async Task Heartbeat_emits_StatusChanged_and_flips_to_Disconnected_on_drop()
    {
        var options = new OwoBackendOptions
        {
            HeartbeatSeconds = 5,
            // No reconnect attempts so the test isn't racing the backoff.
            MaxReconnectAttempts = 0,
        };
        var backend = NewBackend(out var time, out var sdk, options);
        sdk.AutoConnectAsync().Returns(Task.CompletedTask);
        await backend.ConnectAsync(CancellationToken.None);

        // Yield so the heartbeat loop's first Task.Delay registers with the
        // FakeTimeProvider before we advance.
        await Task.Delay(50);

        sdk.IsConnected.Returns(false);
        time.Advance(TimeSpan.FromSeconds(5));

        var enumerator = backend.Events.GetAsyncEnumerator();
        var evt = await NextWithin(enumerator, TimeSpan.FromSeconds(2));

        evt.Should().BeOfType<BackendLifecycleEvent>();
        var lifecycle = (BackendLifecycleEvent)evt;
        lifecycle.Change.Should().Be(BackendLifecycleChange.StatusChanged);
        lifecycle.Reason.Should().Be("transport dropped");
        backend.Status.Should().Be(BackendStatus.Disconnected);

        await backend.DisposeAsync();
    }

    [Fact]
    public async Task Heartbeat_emits_StatusChanged_when_connection_is_restored()
    {
        var options = new OwoBackendOptions
        {
            HeartbeatSeconds = 5,
            MaxReconnectAttempts = 0,
        };
        var backend = NewBackend(out var time, out var sdk, options);
        sdk.AutoConnectAsync().Returns(Task.CompletedTask);
        await backend.ConnectAsync(CancellationToken.None);

        await Task.Delay(50);

        // First tick sees a drop.
        sdk.IsConnected.Returns(false);
        time.Advance(TimeSpan.FromSeconds(5));
        await Task.Delay(50);

        // Second tick sees the recovery.
        sdk.IsConnected.Returns(true);
        time.Advance(TimeSpan.FromSeconds(5));

        var enumerator = backend.Events.GetAsyncEnumerator();
        var lifecycleEvents = new List<BackendLifecycleEvent>();
        for (var i = 0; i < 2; i++)
        {
            var evt = await NextWithin(enumerator, TimeSpan.FromSeconds(2));
            if (evt is BackendLifecycleEvent l) lifecycleEvents.Add(l);
        }

        lifecycleEvents.Should().HaveCount(2);
        lifecycleEvents[0].Reason.Should().Be("transport dropped");
        lifecycleEvents[1].Reason.Should().Be("reconnected");
        backend.Status.Should().Be(BackendStatus.Ready);

        await backend.DisposeAsync();
    }

    [Fact]
    public async Task Reconnect_exhaustion_flips_status_to_Error()
    {
        var options = new OwoBackendOptions
        {
            HeartbeatSeconds = 5,
            MaxReconnectAttempts = 2,
        };
        var backend = NewBackend(out var time, out var sdk, options);
        sdk.AutoConnectAsync().Returns(Task.CompletedTask);
        await backend.ConnectAsync(CancellationToken.None);

        await Task.Delay(50);

        // Trigger the heartbeat to observe the drop.
        sdk.IsConnected.Returns(false);
        time.Advance(TimeSpan.FromSeconds(5));
        await Task.Delay(50);

        // Drain the "transport dropped" event so the next event we pull is
        // the post-exhaustion one.
        var enumerator = backend.Events.GetAsyncEnumerator();
        (await NextWithin(enumerator, TimeSpan.FromSeconds(2)))
            .Should().BeOfType<BackendLifecycleEvent>()
            .Which.Reason.Should().Be("transport dropped");

        // The reconnect path uses 2^n second backoff per attempt. Two
        // failed attempts at attempts 1 and 2 → delays of 2s and 4s.
        // Advance through both.
        time.Advance(TimeSpan.FromSeconds(2));
        await Task.Delay(50);
        time.Advance(TimeSpan.FromSeconds(4));

        var exhausted = await NextWithin(enumerator, TimeSpan.FromSeconds(2));
        exhausted.Should().BeOfType<BackendLifecycleEvent>()
            .Which.Reason.Should().Be("reconnection exhausted");
        backend.Status.Should().Be(BackendStatus.Error);

        await backend.DisposeAsync();
    }

    private static BackendTriggerRequest MakeRequest(string id, TimeSpan duration)
    {
        var values = new Dictionary<string, ParameterValue>
        {
            ["frequency"] = new ParameterValue.Number(80),
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

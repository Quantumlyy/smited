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

public class OwoBackendTests
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
    public void Static_descriptors_match_the_spec()
    {
        var backend = NewBackend(out _, out _);

        backend.Id.Should().Be("owo-primary");
        backend.Kind.Should().Be("owo_skin");
        backend.DisplayName.Should().Be("OWO Skin");
        backend.Status.Should().Be(BackendStatus.Disconnected); // until ConnectAsync runs
        backend.Capabilities.Should().BeEquivalentTo("ems", "zoned", "calibrated");
        backend.Concurrency.MaxConcurrent.Should().Be(1u);
        backend.Concurrency.Policy.Should().Be(ConcurrencyPolicy.CancelOldest);
        backend.Calibration.Should().BeNull();
    }

    [Fact]
    public void Zone_topology_mirrors_the_mock_backend()
    {
        var backend = NewBackend(out _, out _);

        backend.Zones.Zones.Select(z => z.Id).Should().BeEquivalentTo(
            "pectoral_l", "pectoral_r",
            "abdominal_l", "abdominal_r",
            "lumbar_l", "lumbar_r",
            "dorsal_l", "dorsal_r",
            "arm_l", "arm_r");
        backend.Zones.Groups.Select(g => g.Id).Should().BeEquivalentTo("torso", "arms", "all");
    }

    [Fact]
    public async Task ConnectAsync_uses_manual_ip_when_set()
    {
        var options = new OwoBackendOptions { ManualIp = "10.0.0.5" };
        var backend = NewBackend(out _, out var sdk, options);
        sdk.ConnectAsync("10.0.0.5").Returns(Task.CompletedTask);

        await backend.ConnectAsync(CancellationToken.None);

        await sdk.Received(1).ConnectAsync("10.0.0.5");
        await sdk.DidNotReceive().AutoConnectAsync();
    }

    [Fact]
    public async Task ConnectAsync_uses_auto_connect_when_manual_ip_is_unset()
    {
        var backend = NewBackend(out _, out var sdk);
        sdk.AutoConnectAsync().Returns(Task.CompletedTask);

        await backend.ConnectAsync(CancellationToken.None);

        await sdk.Received(1).AutoConnectAsync();
        await sdk.DidNotReceive().ConnectAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task ConnectAsync_sets_Ready_and_seeds_calibration_on_success()
    {
        var backend = NewBackend(out var time, out var sdk);
        sdk.AutoConnectAsync().Returns(Task.CompletedTask);

        await backend.ConnectAsync(CancellationToken.None);

        backend.Status.Should().Be(BackendStatus.Ready);
        backend.Calibration.Should().NotBeNull();
        backend.Calibration!.Calibrated.Should().BeTrue();
        backend.Calibration.LastCalibratedAt.ToDateTimeOffset().Should().Be(time.GetUtcNow());
    }

    [Fact]
    public async Task ConnectAsync_sets_Error_when_sdk_throws()
    {
        var backend = NewBackend(out _, out var sdk);
        sdk.AutoConnectAsync().Returns(Task.FromException(new InvalidOperationException("boom")));

        var act = async () => await backend.ConnectAsync(CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();

        backend.Status.Should().Be(BackendStatus.Error);
    }

    [Fact]
    public async Task ConnectAsync_sets_Error_when_sdk_reports_not_connected()
    {
        var backend = NewBackend(out _, out var sdk);
        sdk.AutoConnectAsync().Returns(Task.CompletedTask);
        sdk.IsConnected.Returns(false);

        var act = async () => await backend.ConnectAsync(CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();

        backend.Status.Should().Be(BackendStatus.Error);
    }

    [Fact]
    public async Task TriggerAsync_throws_when_not_ready()
    {
        var backend = NewBackend(out _, out _);

        var act = async () => await backend.TriggerAsync(MakeRequest("s1", TimeSpan.FromSeconds(1)), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Disconnected*cannot trigger*");
    }

    [Fact]
    public async Task TriggerAsync_emits_Started_and_then_Completed_after_duration()
    {
        var backend = await NewReadyBackend();
        await using var ____ = backend.B;
        var time = backend.Time;

        var result = await backend.B.TriggerAsync(MakeRequest("s1", TimeSpan.FromSeconds(2)), CancellationToken.None);
        result.EstimatedDuration.Should().Be(TimeSpan.FromSeconds(2));

        var enumerator = backend.B.Events.GetAsyncEnumerator();

        (await NextWithin(enumerator, TimeSpan.FromSeconds(1)))
            .Should().BeOfType<SensationStarted>();

        time.Advance(TimeSpan.FromSeconds(2));

        (await NextWithin(enumerator, TimeSpan.FromSeconds(1)))
            .Should().BeOfType<SensationCompleted>();
    }

    [Fact]
    public async Task TriggerAsync_sends_one_command_per_microsensation()
    {
        var backend = await NewReadyBackend();
        await using var ____ = backend.B;

        var result = await backend.B.TriggerAsync(
            MakeRequest("s1", TimeSpan.FromMilliseconds(100)),
            CancellationToken.None);

        // Pump the thread pool so the background Task.Run dispatches the
        // Send call. With one micro and FakeTimeProvider we don't need to
        // advance time before observing the Send — the Send fires before
        // the gating Task.Delay.
        await Task.Delay(50);

        backend.Sdk.Received(1).Send(Arg.Any<OwoSendCommand>());
        result.EstimatedDuration.Should().Be(TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public async Task TriggerAsync_applies_intensity_scale()
    {
        var backend = await NewReadyBackend();
        await using var ____ = backend.B;

        var values = new Dictionary<string, ParameterValue>
        {
            ["frequency"] = new ParameterValue.Number(80),
            ["intensity"] = new ParameterValue.Number(80),
            ["duration"] = new ParameterValue.Duration(TimeSpan.FromMilliseconds(50)),
        };
        var request = new BackendTriggerRequest(
            SensationId: "scaled",
            SensationName: "test",
            ZoneIds: new[] { "pectoral_l" },
            IntensityScale: 50, // Halve the resolved intensity.
            Priority: 0,
            ClientTraceId: "trace",
            Microsensations: new[] { new MicrosensationParameters(values) });

        await backend.B.TriggerAsync(request, CancellationToken.None);
        await Task.Delay(50);

        backend.Sdk.Received(1).Send(Arg.Is<OwoSendCommand>(c => c.IntensityPercentage == 40));
    }

    [Fact]
    public async Task StopAsync_cancels_active_sensation_calls_sdk_stop_and_emits_Cancelled()
    {
        var backend = await NewReadyBackend();
        await using var ____ = backend.B;

        await backend.B.TriggerAsync(MakeRequest("s1", TimeSpan.FromSeconds(5)), CancellationToken.None);

        var enumerator = backend.B.Events.GetAsyncEnumerator();
        (await NextWithin(enumerator, TimeSpan.FromSeconds(1)))
            .Should().BeOfType<SensationStarted>();

        var stopped = await backend.B.StopAsync(
            new BackendStopRequest("s1", All: false), CancellationToken.None);

        stopped.Should().Be(1);
        backend.Sdk.Received().Stop();
        (await NextWithin(enumerator, TimeSpan.FromSeconds(1)))
            .Should().BeOfType<SensationCancelled>();
    }

    [Fact]
    public async Task StopAsync_with_All_cancels_every_in_flight_and_calls_sdk_stop_once()
    {
        var backend = await NewReadyBackend();
        await using var ____ = backend.B;

        await backend.B.TriggerAsync(MakeRequest("s1", TimeSpan.FromSeconds(5)), CancellationToken.None);

        var stopped = await backend.B.StopAsync(
            new BackendStopRequest(SensationId: null, All: true), CancellationToken.None);

        stopped.Should().Be(1);
        backend.Sdk.Received(1).Stop();
    }

    [Fact]
    public async Task StopAsync_with_unknown_sensation_id_is_a_noop()
    {
        var backend = await NewReadyBackend();
        await using var ____ = backend.B;

        var stopped = await backend.B.StopAsync(
            new BackendStopRequest("does-not-exist", All: false), CancellationToken.None);

        stopped.Should().Be(0);
        backend.Sdk.DidNotReceive().Stop();
    }

    [Fact]
    public async Task TriggerAsync_expands_group_zones_to_member_leaves()
    {
        var backend = await NewReadyBackend();
        await using var ____ = backend.B;

        var values = new Dictionary<string, ParameterValue>
        {
            ["frequency"] = new ParameterValue.Number(50),
            ["intensity"] = new ParameterValue.Number(50),
            ["duration"] = new ParameterValue.Duration(TimeSpan.FromMilliseconds(100)),
        };
        var request = new BackendTriggerRequest(
            SensationId: "grouped",
            SensationName: "test",
            ZoneIds: new[] { "arms" }, // Group, must expand to arm_l + arm_r.
            IntensityScale: null,
            Priority: 0,
            ClientTraceId: "trace",
            Microsensations: new[] { new MicrosensationParameters(values) });

        await backend.B.TriggerAsync(request, CancellationToken.None);
        await Task.Delay(50);

        backend.Sdk.Received(1).Send(Arg.Is<OwoSendCommand>(c =>
            c.ZoneIds.Count == 2
            && c.ZoneIds.Contains("arm_l")
            && c.ZoneIds.Contains("arm_r")));
    }

    [Fact]
    public async Task TriggerAsync_dedupes_zones_when_a_group_overlaps_a_leaf()
    {
        var backend = await NewReadyBackend();
        await using var ____ = backend.B;

        var values = new Dictionary<string, ParameterValue>
        {
            ["frequency"] = new ParameterValue.Number(50),
            ["intensity"] = new ParameterValue.Number(50),
            ["duration"] = new ParameterValue.Duration(TimeSpan.FromMilliseconds(100)),
        };
        var request = new BackendTriggerRequest(
            SensationId: "deduped",
            SensationName: "test",
            // arm_l is a member of "arms"; the duplicate must be collapsed.
            ZoneIds: new[] { "arm_l", "arms" },
            IntensityScale: null,
            Priority: 0,
            ClientTraceId: "trace",
            Microsensations: new[] { new MicrosensationParameters(values) });

        await backend.B.TriggerAsync(request, CancellationToken.None);
        await Task.Delay(50);

        backend.Sdk.Received(1).Send(Arg.Is<OwoSendCommand>(c =>
            c.ZoneIds.Count == 2
            && c.ZoneIds.Contains("arm_l")
            && c.ZoneIds.Contains("arm_r")));
    }

    [Fact]
    public async Task TriggerAsync_with_pre_cancelled_token_skips_Send_and_emits_Cancelled()
    {
        // Two regressions in one test:
        //   1) Earlier bug: the dispatch Task.Run was started with the
        //      caller's token, so a pre-cancelled token returned a
        //      pre-cancelled Task without running the body — leaking
        //      _activeSensations and skipping SensationCancelled.
        //   2) Earlier bug: even with the body running, the loop called
        //      _sdk.Send unconditionally before checking the linked
        //      token, so a cancelled trigger still poked the device.
        //      ThrowIfCancellationRequested at the top of each iteration
        //      fixes that.
        var backend = await NewReadyBackend();
        await using var ____ = backend.B;

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await backend.B.TriggerAsync(MakeRequest("cancelled-pre", TimeSpan.FromSeconds(5)), cts.Token);

        var enumerator = backend.B.Events.GetAsyncEnumerator();
        (await NextWithin(enumerator, TimeSpan.FromSeconds(1)))
            .Should().BeOfType<SensationStarted>();
        (await NextWithin(enumerator, TimeSpan.FromSeconds(1)))
            .Should().BeOfType<SensationCancelled>();

        // No Send on a pre-cancelled trigger and no Stop either — there
        // was nothing on the device to silence.
        backend.Sdk.DidNotReceive().Send(Arg.Any<OwoSendCommand>());
        backend.Sdk.DidNotReceive().Stop();

        // Pump once more so the finally block has definitely run, then
        // confirm the entry left the active-sensations dict.
        await Task.Delay(20);
        var stopped = await backend.B.StopAsync(
            new BackendStopRequest("cancelled-pre", All: false), CancellationToken.None);
        stopped.Should().Be(0);
    }

    [Fact]
    public async Task StopAsync_then_TriggerAsync_does_not_silence_the_replacement_via_lingering_old_catch()
    {
        // Regression for the CANCEL_OLDEST race: the coordinator's
        // preempt path is StopAsync(old) followed by TriggerAsync(new).
        // OWO.Stop() is global; if the OLD playback task's OCE catch
        // fires AFTER the NEW playback's first Send, the catch's own
        // _sdk.Stop() silences the replacement.
        var backend = await NewReadyBackend();
        await using var ____ = backend.B;

        await backend.B.TriggerAsync(MakeRequest("old", TimeSpan.FromSeconds(5)), CancellationToken.None);
        await Task.Delay(50); // Let the dispatch loop fire old's first Send.

        backend.Sdk.Received(1).Send(Arg.Any<OwoSendCommand>());
        backend.Sdk.DidNotReceive().Stop();

        // Coordinator preempt: stop the old, immediately trigger the new.
        var stopped = await backend.B.StopAsync(
            new BackendStopRequest("old", All: false), CancellationToken.None);
        stopped.Should().Be(1);
        backend.Sdk.Received(1).Stop();

        await backend.B.TriggerAsync(MakeRequest("new", TimeSpan.FromSeconds(5)), CancellationToken.None);
        await Task.Delay(100); // Let new's Send dispatch AND old's catch run.

        // The new sensation must have been Send'd…
        backend.Sdk.Received(2).Send(Arg.Any<OwoSendCommand>());
        // …and Stop must NOT have been called a second time. If it had,
        // the old playback's OCE catch fired after new's Send and
        // silenced the device.
        backend.Sdk.Received(1).Stop();
    }

    [Fact]
    public async Task TriggerAsync_cancellation_after_first_Send_calls_sdk_Stop()
    {
        // If cancellation arrives after a microsensation has already been
        // dispatched to the SDK, the device is mid-vibration and we have
        // to call _sdk.Stop() to silence it. (StopAsync's own _sdk.Stop()
        // call only happens when StopAsync is the cancellation path —
        // here cancellation is via the caller's CTS directly, simulating
        // a gRPC client abort mid-flight.)
        var backend = await NewReadyBackend();
        await using var ____ = backend.B;

        using var cts = new CancellationTokenSource();

        await backend.B.TriggerAsync(MakeRequest("mid-flight", TimeSpan.FromSeconds(5)), cts.Token);

        // Give the thread pool a chance to dequeue the dispatch loop
        // and fire the first Send before we cancel.
        await Task.Delay(50);
        backend.Sdk.Received(1).Send(Arg.Any<OwoSendCommand>());
        backend.Sdk.DidNotReceive().Stop();

        cts.Cancel();

        var enumerator = backend.B.Events.GetAsyncEnumerator();
        (await NextWithin(enumerator, TimeSpan.FromSeconds(1)))
            .Should().BeOfType<SensationStarted>();
        (await NextWithin(enumerator, TimeSpan.FromSeconds(1)))
            .Should().BeOfType<SensationCancelled>();

        backend.Sdk.Received(1).Stop();
    }

    private record ReadyBackend(OwoBackend B, IOwoSdk Sdk, FakeTimeProvider Time);

    private static async Task<ReadyBackend> NewReadyBackend()
    {
        var backend = NewBackend(out var time, out var sdk);
        sdk.AutoConnectAsync().Returns(Task.CompletedTask);
        await backend.ConnectAsync(CancellationToken.None);
        return new ReadyBackend(backend, sdk, time);
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

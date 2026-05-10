using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Smited.Daemon.Backends;
using Smited.Daemon.Backends.Internal;
using Smited.Daemon.Pishock;
using Smited.V1;
using Xunit;
using ParameterValue = Smited.Daemon.Backends.Internal.ParameterValue;

namespace Smited.Daemon.Tests.Backends.Pishock;

public class PishockBackendTests
{
    private static (PishockBackend backend, FakeClient client, FakeTimeProvider time) NewBackend(
        PishockBackendOptions? options = null,
        string id = "pishock-test")
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 5, 10, 12, 0, 0, TimeSpan.Zero));
        var client = new FakeClient();
        var backend = new PishockBackend(
            id,
            options ?? new PishockBackendOptions(),
            client,
            time,
            NullLogger<PishockBackend>.Instance);
        return (backend, client, time);
    }

    [Fact]
    public void Kind_is_pishock_matching_the_real_hardware_family()
    {
        var (backend, _, _) = NewBackend();
        backend.Kind.Should().Be("pishock");
    }

    [Fact]
    public async Task TriggerAsync_calls_client_with_op_duration_and_intensity()
    {
        var (backend, client, time) = NewBackend();
        await using var __ = backend;

        await backend.TriggerAsync(
            MakeRequest(PishockOp.Vibrate, durationMs: 250, intensity: 40),
            CancellationToken.None);

        // The client call happens inside the playback task; pump time
        // to let it register and fire.
        await PumpUntil(() => client.Calls.Count >= 1, time, TimeSpan.FromSeconds(2));

        client.Calls.Should().HaveCount(1);
        client.Calls[0].Op.Should().Be(PishockOp.Vibrate);
        client.Calls[0].DurationMs.Should().Be(250);
        client.Calls[0].Intensity.Should().Be(40);
    }

    [Fact]
    public async Task TriggerAsync_calls_client_once_per_microsensation_in_a_sequence()
    {
        // LAN mode so the per-pulse playback stays in milliseconds and
        // the test's PumpUntil budget covers the whole sequence.
        var (backend, client, time) = NewBackend(new PishockBackendOptions
        {
            Mode = PishockTransportMode.Lan,
            DeviceIp = "192.168.1.50",
            MaxBurst = 5,
        });
        await using var __ = backend;

        var request = new BackendTriggerRequest(
            SensationId: "seq",
            SensationName: "deploy_success",
            ZoneIds: new[] { "shock" },
            IntensityScale: null,
            Priority: 0,
            ClientTraceId: "trace",
            Microsensations: new[]
            {
                BuildMicro(PishockOp.Vibrate, 100, 30, delayBeforeMs: 0),
                BuildMicro(PishockOp.Vibrate, 100, 30, delayBeforeMs: 200),
                BuildMicro(PishockOp.Vibrate, 100, 30, delayBeforeMs: 200),
            });

        await backend.TriggerAsync(request, CancellationToken.None);

        // The playback task awaits a sequence of Task.Delay(span, time)
        // calls. Each delay is only registered after the previous one
        // unblocks AND the task gets execution time. Interleave wall-time
        // yields with fake-time advances so each successive delay
        // registers and then fires.
        await PumpUntil(() => client.Calls.Count >= 3, time, TimeSpan.FromSeconds(2));

        client.Calls.Should().HaveCount(3);
    }

    [Fact]
    public async Task TriggerAsync_with_disallowed_op_throws_BackendTriggerRejected_without_calling_client()
    {
        var options = new PishockBackendOptions
        {
            AllowedOps = new() { PishockOp.Vibrate },
        };
        var (backend, client, _) = NewBackend(options);
        await using var __ = backend;

        var act = async () => await backend.TriggerAsync(
            MakeRequest(PishockOp.Shock, 200, 10),
            CancellationToken.None);

        var ex = await act.Should().ThrowAsync<BackendTriggerRejectedException>();
        ex.Which.Code.Should().Be(TriggerErrorCode.InvalidParameter);
        client.Calls.Should().BeEmpty(
            "validation must reject before any wire traffic — never fire a disallowed op");
    }

    [Fact]
    public async Task TriggerAsync_when_client_rejects_logs_warning_and_continues()
    {
        var (backend, client, time) = NewBackend();
        await using var __ = backend;

        client.NextResult = new PishockOpResult(false, "Not Authorized", "Not Authorized");

        await backend.TriggerAsync(
            MakeRequest(PishockOp.Vibrate, 200, 30),
            CancellationToken.None);

        await PumpUntil(() => client.Calls.Count >= 1, time, TimeSpan.FromSeconds(2));
        client.Calls.Should().HaveCount(1);
        // The trigger has already returned Accepted to the coordinator
        // by the time the client call happens. The device's NO surfaces
        // as a log line and is documented in pishock.md. A future
        // refinement could emit a structured BackendError event.
    }

    [Fact]
    public async Task TriggerAsync_applies_IntensityScale_to_authored_intensity()
    {
        // The coordinator forwards IntensityScale (a 0..100 percentage) to
        // the backend so callers can attenuate a sensation at trigger time
        // without rewriting the file. PiShock was ignoring it — a caller
        // firing intensity_scale:10 against a 50% vibrate would still
        // send 50% to the device.
        var (backend, client, time) = NewBackend(new PishockBackendOptions
        {
            Mode = PishockTransportMode.Lan,
            DeviceIp = "192.168.1.50",
        });
        await using var __ = backend;

        var values = new Dictionary<string, ParameterValue>
        {
            ["op"] = new ParameterValue.EnumValue(PishockOp.Vibrate.ToString()),
            ["duration"] = new ParameterValue.Duration(TimeSpan.FromMilliseconds(100)),
            ["intensity"] = new ParameterValue.Number(50),
        };
        var request = new BackendTriggerRequest(
            SensationId: "scaled",
            SensationName: "test",
            ZoneIds: new[] { "shock" },
            IntensityScale: 10, // attenuate to 10%
            Priority: 0,
            ClientTraceId: "trace",
            Microsensations: new[] { new MicrosensationParameters(values) });

        await backend.TriggerAsync(request, CancellationToken.None);
        await PumpUntil(() => client.Calls.Count >= 1, time, TimeSpan.FromSeconds(2));

        // 50 * 10 / 100 = 5
        client.Calls[0].Intensity.Should().Be(5);
    }

    [Fact]
    public async Task TriggerAsync_with_no_IntensityScale_passes_authored_intensity_unchanged()
    {
        var (backend, client, time) = NewBackend(new PishockBackendOptions
        {
            Mode = PishockTransportMode.Lan,
            DeviceIp = "192.168.1.50",
        });
        await using var __ = backend;

        await backend.TriggerAsync(
            MakeRequest(PishockOp.Vibrate, durationMs: 100, intensity: 50),
            CancellationToken.None);
        await PumpUntil(() => client.Calls.Count >= 1, time, TimeSpan.FromSeconds(2));

        client.Calls[0].Intensity.Should().Be(50);
    }

    [Fact]
    public async Task TriggerAsync_with_zero_duration_microsensation_does_not_call_client()
    {
        // Schema allows duration=0; treat as a no-op (delay-only step
        // when delay_before is set, otherwise inert). Without this,
        // cloud's whole-second rounding silently turned a 0ms microsensation
        // into a 1-second device fire.
        var (backend, client, time) = NewBackend(new PishockBackendOptions
        {
            Mode = PishockTransportMode.Cloud,
            Username = "u", ApiKey = "k", ShareCode = "s",
        });
        await using var __ = backend;

        var values = new Dictionary<string, ParameterValue>
        {
            ["op"] = new ParameterValue.EnumValue(PishockOp.Vibrate.ToString()),
            ["duration"] = new ParameterValue.Duration(TimeSpan.Zero),
            ["intensity"] = new ParameterValue.Number(50),
        };
        var request = new BackendTriggerRequest(
            SensationId: "z", SensationName: "zero",
            ZoneIds: new[] { "shock" },
            IntensityScale: null, Priority: 0, ClientTraceId: "trace",
            Microsensations: new[] { new MicrosensationParameters(values) });

        await backend.TriggerAsync(request, CancellationToken.None);

        // Pump generously to make sure no client call happens — if the
        // bug were still present, cloud rounding would have turned
        // duration=0 into 1s and a Task.Delay(1s) would queue a client
        // call after the advance.
        await PumpUntil(() => false, time, TimeSpan.FromMilliseconds(500));

        client.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Disposing_the_backend_does_not_throw()
    {
        var (backend, _, _) = NewBackend();
        await backend.DisposeAsync();
    }

    [Fact]
    public async Task TriggerAsync_in_cloud_mode_estimates_duration_rounded_up_to_seconds()
    {
        // The cloud API takes Duration in whole seconds (1..15). A 100ms
        // authored vibrate fires for 1s on the device. If the backend
        // uses the authored 100ms to time its concurrency-slot release,
        // the slot frees while the device is still firing and a follow-up
        // trigger overlaps the in-flight op. EstimatedDuration must
        // reflect the wire reality so the coordinator's slot release
        // matches.
        var (backend, _, _) = NewBackend(new PishockBackendOptions
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
    public async Task TriggerAsync_in_lan_mode_keeps_authored_milliseconds_in_estimate()
    {
        var (backend, _, _) = NewBackend(new PishockBackendOptions
        {
            Mode = PishockTransportMode.Lan,
            DeviceIp = "192.168.1.50",
        });
        await using var __ = backend;

        var result = await backend.TriggerAsync(
            MakeRequest(PishockOp.Vibrate, durationMs: 250, intensity: 30),
            CancellationToken.None);

        result.EstimatedDuration.Should().Be(TimeSpan.FromMilliseconds(250));
    }

    private static BackendTriggerRequest MakeRequest(PishockOp op, int durationMs, int intensity)
    {
        return new BackendTriggerRequest(
            SensationId: $"trigger-{Guid.NewGuid():N}",
            SensationName: "test",
            ZoneIds: new[] { "shock" },
            IntensityScale: null,
            Priority: 0,
            ClientTraceId: "trace",
            Microsensations: new[] { BuildMicro(op, durationMs, intensity, delayBeforeMs: 0) });
    }

    private static MicrosensationParameters BuildMicro(
        PishockOp op, int durationMs, int intensity, int delayBeforeMs)
    {
        var values = new Dictionary<string, ParameterValue>
        {
            ["op"] = new ParameterValue.EnumValue(op.ToString()),
            ["duration"] = new ParameterValue.Duration(TimeSpan.FromMilliseconds(durationMs)),
            ["intensity"] = new ParameterValue.Number(intensity),
        };
        if (delayBeforeMs > 0)
        {
            values["delay_before"] = new ParameterValue.Duration(
                TimeSpan.FromMilliseconds(delayBeforeMs));
        }
        return new MicrosensationParameters(values);
    }

    /// <summary>
    /// Drives a backend whose playback uses sequential
    /// <c>Task.Delay(span, fakeTimeProvider)</c> awaits to completion.
    /// Each iteration yields wall time so the playback task can register
    /// its next delay, then advances fake time so that delay fires.
    /// </summary>
    private static async Task PumpUntil(Func<bool> predicate, FakeTimeProvider time, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!predicate() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Yield();
            await Task.Delay(5);
            time.Advance(TimeSpan.FromMilliseconds(100));
        }
    }

    private sealed class FakeClient : IPishockClient
    {
        public List<(PishockOp Op, int DurationMs, int Intensity)> Calls { get; } = new();

        public PishockOpResult NextResult { get; set; } = new(true, "Operation Succeeded.", null);

        public Task<PishockOpResult> SendOpAsync(
            PishockOp op, int durationMs, int intensity, CancellationToken ct)
        {
            Calls.Add((op, durationMs, intensity));
            return Task.FromResult(NextResult);
        }
    }
}

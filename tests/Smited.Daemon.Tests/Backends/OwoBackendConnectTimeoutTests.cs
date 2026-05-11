// Excluded from compile on non-Windows hosts via the test csproj's
// conditional <Compile Remove>. References OwoBackend, which lives in
// the Windows-only Smited.Daemon.Owo assembly.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Smited.Daemon.Backends;
using Smited.Daemon.Owo;
using Xunit;

namespace Smited.Daemon.Tests.Backends;

public class OwoBackendConnectTimeoutTests
{
    [Fact]
    public async Task ConnectAsync_returns_with_Disconnected_status_when_SDK_does_not_respond_within_deadline()
    {
        var sdk = Substitute.For<IOwoSdk>();
        // Manual-IP path so we hit ConnectAsync(string) rather than
        // AutoConnectAsync(); SDK never resolves the task, simulating a
        // MyOWO/Visualizer that never accepts the game.
        sdk.ConnectAsync(Arg.Any<string>())
            .Returns(_ => new TaskCompletionSource().Task);
        sdk.IsConnected.Returns(false);

        var options = new OwoBackendOptions
        {
            BackendId = "owo-test",
            ManualIp = "127.0.0.1",
            ConnectTimeoutSeconds = 1,
        };
        var backend = new OwoBackend(
            options,
            sdk,
            TimeProvider.System,
            NullLogger<OwoBackend>.Instance);

        // Outer cancellation token is a safety bound — the deadline
        // should fire well before this triggers. If it doesn't, the
        // assertion below catches it via a non-Disconnected status.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await backend.ConnectAsync(cts.Token);

        backend.Status.Should().Be(BackendStatus.Disconnected);
    }

    [Fact]
    public async Task ConnectAsync_after_timeout_kicks_off_reconnect_attempts()
    {
        // Without the timeout-path TryReconnectAsync kickoff, the
        // heartbeat loop only triggers reconnect on a Disconnected ->
        // Ready transition - which never fires when the initial
        // connect itself stayed disconnected. _lastSeenConnected stays
        // false, IsConnected stays false, no transition, no retry.
        // This regression test asserts a real second connect attempt
        // happens after the backoff window.
        var attempts = 0;
        var sdk = Substitute.For<IOwoSdk>();
        sdk.ConnectAsync(Arg.Any<string>())
            .Returns(_ =>
            {
                Interlocked.Increment(ref attempts);
                return new TaskCompletionSource().Task; // never completes
            });
        sdk.IsConnected.Returns(false);

        var time = new FakeTimeProvider(new DateTimeOffset(2026, 5, 10, 0, 0, 0, TimeSpan.Zero));
        var options = new OwoBackendOptions
        {
            BackendId = "owo-test",
            ManualIp = "127.0.0.1",
            ConnectTimeoutSeconds = 1,
            MaxReconnectAttempts = 3,
            HeartbeatSeconds = 5,
        };
        var backend = new OwoBackend(options, sdk, time, NullLogger<OwoBackend>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await backend.ConnectAsync(cts.Token);

        backend.Status.Should().Be(BackendStatus.Disconnected);
        attempts.Should().Be(1, "the initial ConnectAsync attempt fired once before timing out");

        // Advance past the first reconnect attempt's backoff (2^1 = 2s)
        // so TryReconnectAsync's Task.Delay completes and dispatches
        // a fresh connect call to the SDK. FakeTimeProvider fires
        // pending timers synchronously; the continuation runs on the
        // threadpool, so we briefly poll for the second invocation.
        time.Advance(TimeSpan.FromSeconds(3));

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (attempts < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        attempts.Should().BeGreaterThanOrEqualTo(2,
            "TryReconnectAsync should issue a fresh connect call after the backoff fires");

        await backend.DisposeAsync();
    }
}

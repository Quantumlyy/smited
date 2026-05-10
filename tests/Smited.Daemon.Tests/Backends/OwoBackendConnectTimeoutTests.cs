// Excluded from compile on non-Windows hosts via the test csproj's
// conditional <Compile Remove>. References OwoBackend, which lives in
// the Windows-only Smited.Daemon.Owo assembly.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
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
}

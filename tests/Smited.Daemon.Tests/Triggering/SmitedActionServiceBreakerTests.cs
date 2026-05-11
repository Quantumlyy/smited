using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Smited.Daemon.Tests.Fixtures;
using Smited.Daemon.Triggering;
using Xunit;

namespace Smited.Daemon.Tests.Triggering;

/// <summary>
/// Verifies that <see cref="SmitedActionService.PanicAsync"/> latches
/// the daemon-wide breaker. The latching is what turns the panic button
/// from a fire-and-restart stop into a "stop and refuse new triggers
/// until I say so" gate.
/// </summary>
public class SmitedActionServiceBreakerTests : IDisposable
{
    private readonly DaemonFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private SmitedActionService Actions =>
        _fixture.Services.GetRequiredService<SmitedActionService>();

    private IBreakerService Breaker =>
        _fixture.Services.GetRequiredService<IBreakerService>();

    [Fact]
    public async Task PanicAsync_trips_the_breaker()
    {
        Breaker.IsTripped.Should().BeFalse("daemon starts with the breaker untripped");

        await Actions.PanicAsync(TriggerSource.Admin, peer: null, userAgent: null, default);

        Breaker.IsTripped.Should().BeTrue();
        Breaker.TripReason.Should().NotBeNull().And.Contain("Admin",
            "the trip reason carries the source so admin postmortems "
          + "can distinguish UI-initiated panics from gRPC or HTTP ones");
    }

    [Fact]
    public async Task PanicAsync_via_panic_http_source_records_PanicHttp_in_reason()
    {
        await Actions.PanicAsync(TriggerSource.PanicHttp, peer: "127.0.0.1", userAgent: "test", default);

        Breaker.TripReason.Should().Contain("PanicHttp");
    }
}

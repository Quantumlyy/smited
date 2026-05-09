using FluentAssertions;
using Smited.Daemon.Tests.Fixtures;
using Smited.V1;
using Xunit;

namespace Smited.Daemon.Tests.EndToEnd;

public class CapabilityDiscoveryTests : IDisposable
{
    private readonly DaemonFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task ListBackends_returns_the_mock_owo()
    {
        var response = await _fixture.Client.ListBackendsAsync(new ListBackendsRequest());

        response.Backends.Select(b => b.Id).Should().BeEquivalentTo("mock-owo");
        var summary = response.Backends.Single();
        summary.Kind.Should().Be("owo_skin");
        summary.DisplayName.Should().Be("Mock OWO Skin");
        summary.Status.Should().Be(BackendStatus.Ready);
        summary.Capabilities.Should().BeEquivalentTo(
            "ems", "zoned", "calibrated", "sensation_registry_mutable");
    }

    [Fact]
    public async Task ListBackends_filters_by_capability()
    {
        var request = new ListBackendsRequest();
        request.WithCapabilities.Add("ems");

        var response = await _fixture.Client.ListBackendsAsync(request);

        response.Backends.Should().HaveCount(1);
    }

    [Fact]
    public async Task ListBackends_with_unknown_capability_returns_empty()
    {
        var request = new ListBackendsRequest();
        request.WithCapabilities.Add("nonexistent_capability");

        var response = await _fixture.Client.ListBackendsAsync(request);

        response.Backends.Should().BeEmpty();
    }

    [Fact]
    public async Task DescribeBackend_returns_the_full_topology_and_schema()
    {
        var response = await _fixture.Client.DescribeBackendAsync(
            new DescribeBackendRequest { BackendId = "mock-owo" });

        response.Summary.Id.Should().Be("mock-owo");
        response.Zones.Zones.Should().HaveCount(10);
        response.Zones.Groups.Select(g => g.Id).Should().BeEquivalentTo("torso", "arms", "all");
        response.Parameters.Parameters.Select(p => p.Name).Should().BeEquivalentTo(
            "frequency", "intensity", "duration", "ramp_up", "ramp_down", "exit_delay");
        response.Concurrency.MaxConcurrent.Should().Be(1u);
        response.Concurrency.Policy.Should().Be(ConcurrencyPolicy.CancelOldest);
        response.Calibration.Calibrated.Should().BeTrue();
    }

    [Fact]
    public async Task Health_reports_the_running_daemon_with_registered_backends()
    {
        var response = await _fixture.Client.HealthAsync(new HealthRequest());

        response.DaemonRunning.Should().BeTrue();
        response.Backends.Select(b => b.Id).Should().BeEquivalentTo("mock-owo");
    }
}

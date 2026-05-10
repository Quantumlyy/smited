using FluentAssertions;
using Smited.Daemon.Tests.Fixtures;
using Smited.V1;
using Xunit;

namespace Smited.Daemon.Tests.EndToEnd;

public class BhapticsCapabilityTests
{
    [Fact]
    public async Task ListBackends_returns_both_mocks_when_both_enabled()
    {
        using var fixture = new DaemonFixture(enableMockBhaptics: true);

        var response = await fixture.Client.ListBackendsAsync(new ListBackendsRequest());

        response.Backends.Select(b => b.Id).Should().BeEquivalentTo("mock-owo", "mock-bhaptics");
        var bhaptics = response.Backends.Single(b => b.Id == "mock-bhaptics");
        bhaptics.Kind.Should().Be("bhaptics_tactsuit");
        bhaptics.DisplayName.Should().Be("Mock TactSuit X40");
        bhaptics.Status.Should().Be(BackendStatus.Ready);
        bhaptics.Capabilities.Should().Contain("vibration").And.Contain("concurrent_sensations")
            .And.NotContain("calibrated").And.NotContain("ems");
    }

    [Fact]
    public async Task DescribeBackend_mock_bhaptics_returns_full_topology()
    {
        using var fixture = new DaemonFixture(enableMockBhaptics: true);

        var response = await fixture.Client.DescribeBackendAsync(
            new DescribeBackendRequest { BackendId = "mock-bhaptics" });

        response.Summary.Id.Should().Be("mock-bhaptics");
        response.Zones.Zones.Should().HaveCount(40);
        response.Zones.Groups.Select(g => g.Id).Should().BeEquivalentTo(
            "front", "back", "front_chest", "back_shoulders", "torso", "all");
        response.Parameters.Parameters.Select(p => p.Name).Should().BeEquivalentTo(
            "intensity", "duration", "frequency");
        response.Concurrency.MaxConcurrent.Should().Be(4u);
        response.Concurrency.Policy.Should().Be(ConcurrencyPolicy.Priority);
        // bHaptics has no per-user calibration; the optional field is left
        // unset in the response.
        response.Calibration.Should().BeNull();
    }

    [Fact]
    public async Task DescribeBackend_after_SetAccessoriesPresent_returns_expanded_topology()
    {
        using var fixture = new DaemonFixture(enableMockBhaptics: true);

        fixture.MockBhapticsController.SetAccessoriesPresent(true);

        var response = await fixture.Client.DescribeBackendAsync(
            new DescribeBackendRequest { BackendId = "mock-bhaptics" });

        response.Zones.Zones.Should().HaveCount(60);
        response.Zones.Groups.Select(g => g.Id).Should()
            .Contain("gloves").And.Contain("arms");
        response.Zones.Groups.Single(g => g.Id == "all").ZoneIds.Should().HaveCount(60);
    }
}

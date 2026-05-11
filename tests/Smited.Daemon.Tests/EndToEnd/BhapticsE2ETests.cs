using FluentAssertions;
using Smited.Daemon.Tests.Fixtures;
using Smited.V1;
using Xunit;

namespace Smited.Daemon.Tests.EndToEnd;

/// <summary>
/// End-to-end pipeline test for the bHaptics mock backends through
/// the gRPC trigger surface. Verifies the full flow from gRPC client
/// request → SensationLoader-loaded sensation → backend dispatch →
/// motor-payload capture in IMockBhapticsController.
/// </summary>
public class BhapticsE2ETests : IDisposable
{
    private readonly DaemonFixture _fixture;

    public BhapticsE2ETests()
    {
        // Configure two mock bhaptics descriptors plus the default
        // mock-owo so unrelated existing tests don't drift.
        var config = new Dictionary<string, string?>
        {
            ["Smited:Backends:Items:0:Kind"] = "mock_owo",
            ["Smited:Backends:Items:0:Id"] = "mock-owo",
            ["Smited:Backends:Items:0:Enabled"] = "true",
            ["Smited:Backends:Items:1:Kind"] = "mock_bhaptics_vest",
            ["Smited:Backends:Items:1:Id"] = "mock-vest",
            ["Smited:Backends:Items:1:Enabled"] = "true",
            ["Smited:Backends:Items:2:Kind"] = "mock_bhaptics_sleeve_l",
            ["Smited:Backends:Items:2:Id"] = "mock-sleeve-l",
            ["Smited:Backends:Items:2:Enabled"] = "true",
        };

        _fixture = new DaemonFixture(
            seed: root => SampleSensations.WriteBhapticsVest(
                root, "bhaptics_pectoral_pulse.json", SampleSensations.BhapticsVestPectoralPulse),
            additionalConfig: config);
    }

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task Named_sensation_against_mock_bhaptics_vest_succeeds()
    {
        var response = await _fixture.Client.TriggerAsync(new TriggerRequest
        {
            BackendId = "mock-vest",
            SensationName = "bhaptics_pectoral_pulse",
            ClientTraceId = "trace-vest-1",
        });

        response.Accepted.Should().BeTrue();
        response.SensationId.Should().NotBeNullOrEmpty();
        response.ClientTraceId.Should().Be("trace-vest-1");
    }

    [Fact]
    public async Task Trigger_captures_expected_40_byte_motor_payload_on_pectoral_l()
    {
        _fixture.MockBhapticsVest.ClearSubmissions();

        var response = await _fixture.Client.TriggerAsync(new TriggerRequest
        {
            BackendId = "mock-vest",
            SensationName = "bhaptics_pectoral_pulse",
            ClientTraceId = "trace-payload",
        });
        response.Accepted.Should().BeTrue();

        // The mock captures the payload synchronously inside TriggerAsync
        // so a small await for the gRPC roundtrip is enough.
        await Task.Delay(50);

        var submissions = _fixture.MockBhapticsVest.RecentSubmissions;
        submissions.Should().ContainSingle();
        var sub = submissions[0];
        sub.DeviceKey.Should().Be("vest");
        sub.MotorIntensities.Length.Should().Be(40);
        // pectoral_l = motors 0, 1, 4, 5; the other 36 motors stay zero.
        // The sensation file declares default_intensity=50 AND each
        // microsensation declares intensity=50; TriggerCoordinator passes
        // default_intensity as the request's IntensityScale, so the
        // resolved per-motor value is microsensation.intensity * scale / 100
        // = 50 * 50 / 100 = 25.
        sub.MotorIntensities[0].Should().Be(25);
        sub.MotorIntensities[1].Should().Be(25);
        sub.MotorIntensities[4].Should().Be(25);
        sub.MotorIntensities[5].Should().Be(25);
        sub.MotorIntensities[2].Should().Be(0);
        sub.MotorIntensities[20].Should().Be(0);
        sub.Duration.Should().Be(TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public async Task Sensation_targeting_wrong_kind_is_not_registered_on_other_backends()
    {
        // The seeded sensation has backend_kind=mock_bhaptics_vest, so it
        // binds to the vest backend only. Firing the same name against
        // mock-owo (a registered backend) must miss with SENSATION_NOT_FOUND.
        var response = await _fixture.Client.TriggerAsync(new TriggerRequest
        {
            BackendId = "mock-owo",
            SensationName = "bhaptics_pectoral_pulse",
            ClientTraceId = "trace-wrong-kind",
        });

        response.Accepted.Should().BeFalse();
        response.Error.Code.Should().Be(TriggerErrorCode.SensationNotFound);
    }

    [Fact]
    public async Task ListBackends_includes_every_registered_bhaptics_kind()
    {
        // Tests against the gRPC capability surface. Both mock-vest and
        // mock-sleeve-l must appear after BackendBootstrapper has dispatched
        // their factories.
        var listed = await _fixture.Client.ListBackendsAsync(new ListBackendsRequest());
        var ids = listed.Backends.Select(b => b.Id).ToList();

        ids.Should().Contain(new[] { "mock-owo", "mock-vest", "mock-sleeve-l" });
    }

    [Fact]
    public async Task Sleeve_left_backend_advertises_arm_l_zone()
    {
        var response = await _fixture.Client.DescribeBackendAsync(
            new DescribeBackendRequest { BackendId = "mock-sleeve-l" });

        response.Summary.Kind.Should().Be("mock_bhaptics_sleeve_l");
        response.Zones.Zones.Select(z => z.Id).Should().Contain("arm_l");
        response.Parameters.Parameters.Select(p => p.Name).Should().NotContain("frequency",
            "bHaptics is vibrotactile, no frequency parameter");
    }
}

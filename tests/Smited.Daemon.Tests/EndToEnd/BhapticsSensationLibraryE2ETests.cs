using FluentAssertions;
using Smited.Daemon.Tests.Fixtures;
using Smited.V1;
using Xunit;

namespace Smited.Daemon.Tests.EndToEnd;

/// <summary>
/// Verifies the shipped <c>sensations/bhaptics_*</c> library loads
/// cleanly when the matching mock backends are enabled. Catches
/// regressions in the sensation files (missing parameter, wrong
/// zone id, drift between <c>estimated_duration</c> and the
/// envelope sum, accidental <c>frequency</c> parameter, etc.).
/// </summary>
public class BhapticsSensationLibraryE2ETests : IDisposable
{
    private readonly DaemonFixture _fixture;

    public BhapticsSensationLibraryE2ETests()
    {
        // All five mock bhaptics backends enabled so every sensations/
        // bhaptics_* directory has a backend to bind to. The fixture
        // points the library root at the real repo sensations/ tree
        // (relative to the test binary's working directory) so the
        // shipped files are the ones under test.
        var libraryRoot = Path.Combine(AppContext.BaseDirectory, "sensations");
        var config = new Dictionary<string, string?>
        {
            ["Smited:Backends:Items:0:Kind"] = "mock_bhaptics_vest",
            ["Smited:Backends:Items:0:Id"] = "mock-vest",
            ["Smited:Backends:Items:0:Enabled"] = "true",
            ["Smited:Backends:Items:1:Kind"] = "mock_bhaptics_sleeve_l",
            ["Smited:Backends:Items:1:Id"] = "mock-sleeve-l",
            ["Smited:Backends:Items:1:Enabled"] = "true",
            ["Smited:Backends:Items:2:Kind"] = "mock_bhaptics_sleeve_r",
            ["Smited:Backends:Items:2:Id"] = "mock-sleeve-r",
            ["Smited:Backends:Items:2:Enabled"] = "true",
            ["Smited:Backends:Items:3:Kind"] = "mock_bhaptics_feet_l",
            ["Smited:Backends:Items:3:Id"] = "mock-feet-l",
            ["Smited:Backends:Items:3:Enabled"] = "true",
            ["Smited:Backends:Items:4:Kind"] = "mock_bhaptics_feet_r",
            ["Smited:Backends:Items:4:Id"] = "mock-feet-r",
            ["Smited:Backends:Items:4:Enabled"] = "true",
        };

        _fixture = new DaemonFixture(libraryRoot: libraryRoot, additionalConfig: config);
    }

    public void Dispose() => _fixture.Dispose();

    [Theory]
    [InlineData("mock-vest", "compile_error_mild")]
    [InlineData("mock-vest", "compile_error_severe")]
    [InlineData("mock-vest", "deploy_success")]
    [InlineData("mock-vest", "test_failed")]
    [InlineData("mock-sleeve-l", "chat_tap")]
    [InlineData("mock-sleeve-l", "chat_zap")]
    [InlineData("mock-feet-l", "pager_alert")]
    public async Task Shipped_sensation_triggers_successfully(string backendId, string sensationName)
    {
        var response = await _fixture.Client.TriggerAsync(new TriggerRequest
        {
            BackendId = backendId,
            SensationName = sensationName,
            ClientTraceId = $"trace-{sensationName}",
        });

        response.Accepted.Should().BeTrue(
            $"{sensationName} should load and trigger on {backendId}; gRPC error: {response.Error?.Code} {response.Error?.Message}");
    }

    [Fact]
    public async Task Vest_sensations_do_not_declare_frequency_parameter()
    {
        // The bhaptics ParameterSchema omits frequency. If any sensation
        // file slipped in a frequency parameter, SensationLoader boot
        // would have failed and the fixture ctor would have thrown.
        // Already implicit in the fact that the fixture constructed
        // successfully; this assert is a backstop in case a future
        // refactor moves validation later.
        var listed = await _fixture.Client.ListSensationsAsync(new ListSensationsRequest
        {
            BackendId = "mock-vest",
        });

        listed.Sensations.Should().NotBeEmpty();
        foreach (var sensation in listed.Sensations)
        {
            sensation.Definition.Microsensations.Should().AllSatisfy(m =>
                m.Parameters.Should().NotContainKey("frequency",
                    $"sensation {sensation.Name} must not declare frequency on a bhaptics backend"));
        }
    }
}

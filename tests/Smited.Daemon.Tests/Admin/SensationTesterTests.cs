using FluentAssertions;
using Smited.Daemon.Admin.Components.Pages;
using Xunit;

namespace Smited.Daemon.Tests.Admin;

public class SensationTesterTests
{
    /// <summary>
    /// The wire contract documents <c>intensity_scale</c> as 0..100 and
    /// protovalidate rejects out-of-range values on the gRPC path. The
    /// admin UI's tester bypasses protovalidate (it builds
    /// <see cref="Smited.Daemon.Triggering.ResolvedTriggerInput"/> in-
    /// process), so the clamp lives in <c>BuildInput</c> instead. Values
    /// above 100 must collapse to 100 so the admin path produces results
    /// that are reproducible via gRPC and that respect a user's
    /// calibration ceiling on real hardware.
    /// </summary>
    [Theory]
    [InlineData(0u, 0u)]
    [InlineData(50u, 50u)]
    [InlineData(100u, 100u)]
    [InlineData(101u, 100u)]
    [InlineData(150u, 100u)]
    [InlineData(200u, 100u)]
    [InlineData(uint.MaxValue, 100u)]
    public void BuildInput_clamps_intensity_to_contract_range(
        uint inputIntensity,
        uint expectedIntensity)
    {
        var result = SensationTester.BuildInput(
            backendId: "mock-owo",
            sensationName: "compile_error_mild",
            intensityScalePct: inputIntensity,
            priority: 0,
            traceId: "test");

        result.IntensityScale.Should().Be(expectedIntensity);
    }

    [Fact]
    public void BuildInput_uses_provided_trace_id_when_non_empty()
    {
        var result = SensationTester.BuildInput(
            backendId: "mock-owo",
            sensationName: "compile_error_mild",
            intensityScalePct: 50,
            priority: 0,
            traceId: "trace-from-user");

        result.ClientTraceId.Should().Be("trace-from-user");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void BuildInput_generates_a_trace_id_when_empty(string? traceId)
    {
        var result = SensationTester.BuildInput(
            backendId: "mock-owo",
            sensationName: "compile_error_mild",
            intensityScalePct: 50,
            priority: 0,
            traceId: traceId);

        result.ClientTraceId.Should().NotBeNullOrEmpty();
    }
}

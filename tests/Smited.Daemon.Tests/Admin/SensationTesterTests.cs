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
    /// process), so the clamp lives in <c>BuildInput</c> instead.
    ///
    /// The first row locks in Round-N+2 fix #1: when the user leaves the
    /// override field empty, <c>BuildInput</c> must pass <c>null</c>
    /// through so the coordinator applies the sensation's authored
    /// <c>default_intensity</c>. Eager-defaulting to <c>100</c> erased
    /// the distinction between "no override" and "override to max."
    ///
    /// The remaining rows lock in the existing clamp: explicit values
    /// above 100 collapse to 100 so the admin path produces results
    /// that are reproducible via gRPC and that respect a user's
    /// calibration ceiling on real hardware.
    /// </summary>
    [Theory]
    [InlineData(null, null)]                  // unset → unset (regression test for fix #1)
    [InlineData(0u, 0u)]                      // explicit 0 → 0 (deliberate silent trigger)
    [InlineData(50u, 50u)]                    // typical override
    [InlineData(100u, 100u)]                  // max within contract
    [InlineData(101u, 100u)]                  // just above contract clamped
    [InlineData(150u, 100u)]                  // out-of-contract clamped
    [InlineData(200u, 100u)]                  // out-of-contract clamped
    [InlineData(uint.MaxValue, 100u)]         // pathological clamped
    public void BuildInput_passes_intensity_through_clamp(
        uint? inputIntensity,
        uint? expectedIntensity)
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

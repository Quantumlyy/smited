using FluentAssertions;
using Smited.Daemon.Pishock;
using Smited.Daemon.Pishock.Internal;
using Xunit;
using ParameterValue = Smited.Daemon.Backends.Internal.ParameterValue;

namespace Smited.Daemon.Tests.Backends.Pishock;

/// <summary>
/// Adaptation rules for the admin body map's click-to-fire diagnostic on
/// PiShock: a hardcoded vibrate@60 always rejected on legitimate
/// non-default configs (Beep-only descriptors, <c>MaxIntensityVibrate</c>
/// capped below 60, tight <c>MaxDurationMs</c>). The diagnostic now
/// reads the descriptor's options and picks the safest op + intensity
/// + duration that fits.
/// </summary>
public class PishockDiagnosticTests
{
    [Fact]
    public void Default_options_yield_vibrate_at_60_for_300ms()
    {
        var diag = PishockDescriptors.BuildDiagnosticMicrosensation(new PishockBackendOptions());

        diag.Values["op"].Should().BeOfType<ParameterValue.EnumValue>()
            .Which.Value.Should().Be("vibrate");
        diag.Values["intensity"].Should().BeOfType<ParameterValue.Number>()
            .Which.Value.Should().Be(60);
        diag.Values["duration"].Should().BeOfType<ParameterValue.Duration>()
            .Which.Value.Should().Be(TimeSpan.FromMilliseconds(300));
    }

    /// <summary>
    /// Pre-fix bug: a Beep-and-Shock-only descriptor would have its
    /// click-to-fire rejected because the diagnostic always asked for
    /// vibrate. Post-fix the diagnostic falls back to Beep — still
    /// useful for "did the daemon hit the right device" confirmation
    /// even though it's audio rather than haptic.
    /// </summary>
    [Fact]
    public void Beep_only_descriptor_falls_back_to_beep_not_vibrate()
    {
        var options = new PishockBackendOptions
        {
            AllowedOps = new List<PishockOp> { PishockOp.Beep },
        };

        var diag = PishockDescriptors.BuildDiagnosticMicrosensation(options);

        diag.Values["op"].Should().BeOfType<ParameterValue.EnumValue>()
            .Which.Value.Should().Be("beep");
    }

    [Fact]
    public void Beep_and_shock_descriptor_prefers_beep_over_shock()
    {
        var options = new PishockBackendOptions
        {
            AllowedOps = new List<PishockOp> { PishockOp.Beep, PishockOp.Shock },
        };

        var diag = PishockDescriptors.BuildDiagnosticMicrosensation(options);

        diag.Values["op"].Should().BeOfType<ParameterValue.EnumValue>()
            .Which.Value.Should().Be("beep",
                "Shock is the highest-risk op; diagnostic must never pick it when a safer alternative is allowed");
    }

    /// <summary>
    /// Last-resort fallback: when AllowedOps somehow contains only
    /// Shock, the diagnostic uses Shock but clamps intensity to
    /// MaxIntensityShock so the operator's safety ceiling still applies.
    /// </summary>
    [Fact]
    public void Shock_only_descriptor_uses_shock_clamped_to_MaxIntensityShock()
    {
        var options = new PishockBackendOptions
        {
            AllowedOps = new List<PishockOp> { PishockOp.Shock },
            // Default MaxIntensityShock=30; explicit for clarity.
            MaxIntensityShock = 30,
        };

        var diag = PishockDescriptors.BuildDiagnosticMicrosensation(options);

        diag.Values["op"].Should().BeOfType<ParameterValue.EnumValue>().Which.Value.Should().Be("shock");
        diag.Values["intensity"].Should().BeOfType<ParameterValue.Number>().Which.Value.Should().Be(30,
            "the configured shock ceiling must override the 60 default");
    }

    /// <summary>
    /// Pre-fix bug: a descriptor with MaxIntensityVibrate=20 would
    /// always reject the click-to-fire because the diagnostic asked for
    /// 60. Post-fix the diagnostic clamps to the per-op ceiling.
    /// </summary>
    [Fact]
    public void MaxIntensityVibrate_below_60_clamps_the_diagnostic_intensity()
    {
        var options = new PishockBackendOptions { MaxIntensityVibrate = 20 };

        var diag = PishockDescriptors.BuildDiagnosticMicrosensation(options);

        diag.Values["intensity"].Should().BeOfType<ParameterValue.Number>().Which.Value.Should().Be(20);
    }

    [Fact]
    public void MaxDurationMs_below_300_clamps_the_diagnostic_duration()
    {
        var options = new PishockBackendOptions { MaxDurationMs = 150 };

        var diag = PishockDescriptors.BuildDiagnosticMicrosensation(options);

        diag.Values["duration"].Should().BeOfType<ParameterValue.Duration>()
            .Which.Value.Should().Be(TimeSpan.FromMilliseconds(150));
    }

    /// <summary>
    /// Round-trip check: the diagnostic produced for a constrained
    /// descriptor (vibrate-only, tight intensity + duration caps) must
    /// pass the same PishockTriggerValidator that the trigger pipeline
    /// runs at fire time. Pre-fix this was the load-bearing claim that
    /// failed; this test is the regression gate.
    /// </summary>
    [Fact]
    public void Diagnostic_passes_PishockTriggerValidator_under_default_options()
    {
        var options = new PishockBackendOptions();
        var diag = PishockDescriptors.BuildDiagnosticMicrosensation(options);

        var act = () => PishockTriggerValidator.ValidateMicrosensation(
            index: 0, micro: diag, intensityScale: 100u, options: options);
        act.Should().NotThrow();
    }

    [Fact]
    public void Diagnostic_passes_PishockTriggerValidator_under_constrained_options()
    {
        // The exact scenario the original code review called out:
        // vibrate excluded from AllowedOps, MaxIntensityVibrate
        // implicitly irrelevant because Beep is chosen. Diagnostic must
        // still pass validation.
        var options = new PishockBackendOptions
        {
            AllowedOps = new List<PishockOp> { PishockOp.Beep },
            MaxIntensityVibrate = 10, // would have failed pre-fix at intensity=60
        };
        var diag = PishockDescriptors.BuildDiagnosticMicrosensation(options);

        var act = () => PishockTriggerValidator.ValidateMicrosensation(
            index: 0, micro: diag, intensityScale: 100u, options: options);
        act.Should().NotThrow();
    }
}

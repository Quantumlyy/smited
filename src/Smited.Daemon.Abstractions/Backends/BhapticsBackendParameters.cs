using Smited.V1;

namespace Smited.Daemon.Backends;

/// <summary>
/// Builds the <see cref="ParameterSchema"/> shared by every bHaptics
/// backend kind (real and mock alike). Five parameters:
/// <c>intensity</c>, <c>duration</c>, <c>ramp_up</c>, <c>ramp_down</c>,
/// <c>exit_delay</c>. NO <c>frequency</c> — bHaptics is vibrotactile,
/// not EMS, and the SDK has no frequency knob.
/// </summary>
/// <remarks>
/// Cross-platform (lives in Abstractions) so the mock backends in
/// <c>Smited.Daemon.Backends.Mock</c> share the exact same parameter
/// surface the real Windows-only backends advertise. Sensation files
/// that declare <c>frequency</c> against any <c>bhaptics_*</c> kind
/// fail at <c>SensationLoader</c> boot via the existing "parameter
/// not declared by backend" validation rule.
/// </remarks>
public static class BhapticsBackendParameters
{
    public static ParameterSchema Build()
    {
        var s = new ParameterSchema();
        s.Parameters.Add(MakeNumber("intensity", required: true, min: 0, max: 100, unit: "%",
            description: "Vibration intensity (% of motor maximum)"));
        s.Parameters.Add(MakeDuration("duration", required: true, min: 0, max: 10,
            description: "Active vibration length"));
        s.Parameters.Add(MakeDuration("ramp_up", required: false, min: 0, max: 5,
            description: "Quiet pre-pulse spacing"));
        s.Parameters.Add(MakeDuration("ramp_down", required: false, min: 0, max: 5,
            description: "Quiet post-pulse spacing"));
        s.Parameters.Add(MakeDuration("exit_delay", required: false, min: 0, max: 5,
            description: "Quiet trailing delay"));
        return s;
    }

    private static ParameterDef MakeNumber(
        string name, bool required, double min, double max, string unit, string description) =>
        new()
        {
            Name = name,
            Type = ParameterType.Number,
            Required = required,
            Min = min,
            Max = max,
            Unit = unit,
            Description = description,
        };

    private static ParameterDef MakeDuration(
        string name, bool required, double min, double max, string description) =>
        new()
        {
            Name = name,
            Type = ParameterType.Duration,
            Required = required,
            Min = min,
            Max = max,
            Description = description,
        };
}

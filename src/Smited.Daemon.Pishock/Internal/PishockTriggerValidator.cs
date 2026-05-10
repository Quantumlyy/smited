using Smited.Daemon.Backends;
using Smited.Daemon.Backends.Internal;
using Smited.V1;

namespace Smited.Daemon.Pishock.Internal;

/// <summary>
/// Per-instance trigger validation that the coordinator's generic
/// <c>ParameterSchema</c> + <c>ZoneTopology</c> validation can't
/// express on its own — specifically the <c>AllowedOps</c> allow-list
/// (per descriptor, schema only carries the enum values) and the
/// per-op intensity ceilings (schema's <c>Max</c> is a single value,
/// not a per-op map). Throws <see cref="BackendTriggerRejectedException"/>
/// on the first violation; the coordinator catches it and surfaces the
/// rejection on the wire.
/// </summary>
internal static class PishockTriggerValidator
{
    public static void ValidateMicrosensation(
        int index, MicrosensationParameters micro, PishockBackendOptions options)
    {
        ArgumentNullException.ThrowIfNull(micro);
        ArgumentNullException.ThrowIfNull(options);

        var op = MicrosensationReader.ReadOp(micro);
        if (!options.AllowedOps.Contains(op))
        {
            throw new BackendTriggerRejectedException(
                TriggerErrorCode.InvalidParameter,
                $"op '{op}' is not in this descriptor's AllowedOps "
                + $"({string.Join(", ", options.AllowedOps)})",
                $"microsensations[{index}].parameters.op");
        }

        var intensity = (int)MicrosensationReader.ReadNumber(micro, "intensity");
        var cap = op switch
        {
            PishockOp.Shock => options.MaxIntensityShock,
            PishockOp.Vibrate => options.MaxIntensityVibrate,
            // Beep is acoustic only; the cap is informational, mirror
            // the schema's 0..100 range without imposing a tighter
            // daemon-level ceiling.
            PishockOp.Beep => 100,
            _ => 0,
        };
        if (intensity > cap)
        {
            throw new BackendTriggerRejectedException(
                TriggerErrorCode.InvalidParameter,
                $"intensity {intensity} exceeds {op} cap of {cap}",
                $"microsensations[{index}].parameters.intensity");
        }

        var duration = MicrosensationReader.ReadDuration(micro, "duration");
        var maxDuration = TimeSpan.FromMilliseconds(options.MaxDurationMs);
        if (duration > maxDuration)
        {
            throw new BackendTriggerRejectedException(
                TriggerErrorCode.InvalidParameter,
                $"duration {duration.TotalMilliseconds}ms exceeds cap of {options.MaxDurationMs}ms",
                $"microsensations[{index}].parameters.duration");
        }
    }
}

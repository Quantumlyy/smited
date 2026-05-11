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
        int index,
        MicrosensationParameters micro,
        uint? intensityScale,
        PishockBackendOptions options)
    {
        ArgumentNullException.ThrowIfNull(micro);
        ArgumentNullException.ThrowIfNull(options);

        var op = MicrosensationReader.ReadOp(micro);
        var allowed = options.EffectiveAllowedOps;
        if (!allowed.Contains(op))
        {
            throw new BackendTriggerRejectedException(
                TriggerErrorCode.InvalidParameter,
                $"op '{op}' is not in this descriptor's AllowedOps "
                + $"({string.Join(", ", allowed)})",
                $"microsensations[{index}].parameters.op");
        }

        var authoredIntensity = (int)MicrosensationReader.ReadNumber(micro, "intensity");
        // Compare the EFFECTIVE intensity (after IntensityScale is
        // applied) to the per-op cap. Playback sends the scaled value
        // to the device, not the authored one — checking authored
        // against the cap would paradoxically tighten the configured
        // ceiling, rejecting triggers like authored=60 + scale=50
        // against cap=50 even though the wire fires at 30.
        var effectiveIntensity = MicrosensationReader.ApplyIntensityScale(
            authoredIntensity, intensityScale);
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
        if (effectiveIntensity > cap)
        {
            var detail = effectiveIntensity == authoredIntensity
                ? $"{authoredIntensity}"
                : $"{authoredIntensity} (effective {effectiveIntensity} after IntensityScale)";
            throw new BackendTriggerRejectedException(
                TriggerErrorCode.InvalidParameter,
                $"intensity {detail} exceeds {op} cap of {cap}",
                $"microsensations[{index}].parameters.intensity");
        }

        var authoredDuration = MicrosensationReader.ReadDuration(micro, "duration");
        // Compare the EFFECTIVE on-wire duration to the cap, not the
        // authored value. Cloud rounds positive sub-second durations
        // up to whole seconds, so an authored 1500ms vibrate with a
        // 1500ms cap would pass authored-only validation and then
        // fire as 2s on the device — silently exceeding the
        // configured safety ceiling. LAN's effective == authored so
        // the LAN path is unchanged.
        var effectiveDuration = PishockDurationPolicy.Effective(options.Mode, authoredDuration);
        var maxDuration = TimeSpan.FromMilliseconds(options.MaxDurationMs);
        if (effectiveDuration > maxDuration)
        {
            var detail = effectiveDuration == authoredDuration
                ? $"{authoredDuration.TotalMilliseconds}ms"
                : $"{authoredDuration.TotalMilliseconds}ms (effective {effectiveDuration.TotalMilliseconds}ms via {options.Mode})";
            throw new BackendTriggerRejectedException(
                TriggerErrorCode.InvalidParameter,
                $"duration {detail} exceeds cap of {options.MaxDurationMs}ms",
                $"microsensations[{index}].parameters.duration");
        }
    }
}

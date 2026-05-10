using Smited.Daemon.Backends.Internal;
using ParameterValue = Smited.Daemon.Backends.Internal.ParameterValue;

namespace Smited.Daemon.Pishock.Internal;

/// <summary>
/// Tiny readers that pull strongly-typed PiShock parameters out of the
/// generic <see cref="MicrosensationParameters"/> bag. Shared by the
/// mock and real backends so the wire shape is parsed exactly once.
/// </summary>
internal static class MicrosensationReader
{
    public static PishockOp ReadOp(MicrosensationParameters micro)
    {
        if (micro.Values.TryGetValue("op", out var v) && v is ParameterValue.EnumValue e
            && System.Enum.TryParse<PishockOp>(e.Value, ignoreCase: true, out var op))
        {
            return op;
        }
        // The schema marks `op` as required+enum so the coordinator's
        // upstream validation rejects malformed triggers before they
        // reach a backend. A test or admin-UI caller bypassing that
        // validation hits this default; the validator's AllowedOps
        // check then surfaces a structured rejection downstream.
        return PishockOp.Vibrate;
    }

    public static double ReadNumber(MicrosensationParameters micro, string key) =>
        micro.Values.TryGetValue(key, out var v) && v is ParameterValue.Number n ? n.Value : 0;

    public static TimeSpan ReadDuration(MicrosensationParameters micro, string key) =>
        micro.Values.TryGetValue(key, out var v) && v is ParameterValue.Duration d ? d.Value : TimeSpan.Zero;

    public static TimeSpan ComputeEstimatedDuration(
        BackendTriggerRequest request, PishockTransportMode mode)
    {
        var total = TimeSpan.Zero;
        foreach (var micro in request.Microsensations)
        {
            total += ReadDuration(micro, "delay_before")
                + PishockDurationPolicy.Effective(mode, ReadDuration(micro, "duration"));
        }
        return total;
    }
}

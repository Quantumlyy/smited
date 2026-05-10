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

    /// <summary>
    /// Applies the trigger's <see cref="BackendTriggerRequest.IntensityScale"/>
    /// to a microsensation's authored intensity. <c>scale</c> is a
    /// 0..100 percentage; <c>null</c> means "no override, use authored
    /// as-is." Out-of-range values are clamped before scaling so a
    /// caller can't push effective intensity above the per-op cap.
    /// </summary>
    public static int ApplyIntensityScale(int authored, uint? scale)
    {
        if (!scale.HasValue)
        {
            return authored;
        }
        var clamped = Math.Clamp((int)scale.Value, 0, 100);
        return (int)Math.Round(authored * clamped / 100.0);
    }

    /// <summary>
    /// Computes the estimated wall-clock playback duration. The
    /// optional <paramref name="httpBudgetMs"/> is added once per
    /// fireable microsensation so the coordinator's slot-release timer
    /// also covers the per-pulse HTTP round-trip — without that
    /// padding, a slow request can still be in flight when the slot
    /// opens and a follow-up trigger races on the same shocker. The
    /// mock backend passes <c>0</c> (no real HTTP); the real backend
    /// passes <see cref="PishockBackendOptions.RequestTimeoutMs"/>.
    /// Zero-duration microsensations are skipped (they're delay-only
    /// no-ops, never reach the client).
    /// </summary>
    public static TimeSpan ComputeEstimatedDuration(
        BackendTriggerRequest request, PishockTransportMode mode, int httpBudgetMs = 0)
    {
        var total = TimeSpan.Zero;
        foreach (var micro in request.Microsensations)
        {
            total += ReadDuration(micro, "delay_before");
            var duration = ReadDuration(micro, "duration");
            if (duration > TimeSpan.Zero)
            {
                if (httpBudgetMs > 0)
                {
                    total += TimeSpan.FromMilliseconds(httpBudgetMs);
                }
                total += PishockDurationPolicy.Effective(mode, duration);
            }
        }
        return total;
    }
}

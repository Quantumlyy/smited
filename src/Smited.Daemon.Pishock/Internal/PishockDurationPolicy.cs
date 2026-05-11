namespace Smited.Daemon.Pishock.Internal;

/// <summary>
/// Translates an authored microsensation duration into the duration
/// the device actually fires for over the wire. The cloud API takes
/// whole seconds (1..15), so sub-second authoring rounds up; the LAN
/// API takes milliseconds and passes them through.
/// </summary>
/// <remarks>
/// The backend's playback timing and <c>BackendTriggerResult.EstimatedDuration</c>
/// must reflect the wire reality, not the authored intent: a 100ms
/// cloud vibrate fires for 1s on the device, and freeing the
/// concurrency slot at 100ms would let a follow-up trigger overlap
/// the still-firing op on the supposedly single-channel hardware.
/// </remarks>
internal static class PishockDurationPolicy
{
    public static TimeSpan Effective(PishockTransportMode mode, TimeSpan authored)
    {
        // Zero authored duration is a no-op (delay-only step when
        // delay_before is set, otherwise inert). Both transports
        // preserve that — without this guard, cloud's Math.Max(1, ...)
        // would round 0 up to 1s and silently fire a 1-second pulse
        // on a microsensation the user authored as silent.
        if (authored <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }
        if (mode == PishockTransportMode.Cloud)
        {
            // Cloud API takes Duration in whole seconds, range 1..15.
            // Round UP from the authored ms — undershoot would silently
            // fire a shorter op than the user authored, more surprising
            // than firing a slightly longer one. 200ms rounds to 1s,
            // 1100ms rounds to 2s.
            var seconds = (int)Math.Ceiling(authored.TotalMilliseconds / 1000.0);
            return TimeSpan.FromSeconds(seconds);
        }
        return authored;
    }
}

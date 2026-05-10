namespace Smited.Daemon.Pishock;

/// <summary>
/// PiShock operation type. The numeric values match the manufacturer's
/// HTTP-API <c>op</c> field (0 = Shock, 1 = Vibrate, 2 = Beep) so the
/// enum casts directly into the wire payload without a translation table.
/// </summary>
public enum PishockOp
{
    /// <summary>Electrical shock — the highest-risk op. Disabled by default in <c>AllowedOps</c>.</summary>
    Shock = 0,

    /// <summary>Vibration — low-risk haptic feedback. Enabled by default.</summary>
    Vibrate = 1,

    /// <summary>Audible beep — no electrical or mechanical stimulation. Enabled by default.</summary>
    Beep = 2,
}

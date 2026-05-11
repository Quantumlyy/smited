namespace Smited.Daemon.Backends;

/// <summary>
/// Configuration for the <c>bhaptics_vest</c> backend (TactSuit X40,
/// 40 actuators). Currently has no vest-specific fields; the type
/// exists as a distinct binding target so a future vest-only setting
/// (e.g. a per-actuator vibration profile) can land here without
/// breaking config bindings on the sleeve/feet kinds.
/// </summary>
public sealed class BhapticsVestOptions : BhapticsBackendOptionsBase
{
    public BhapticsVestOptions()
    {
        BackendId = "bhaptics-vest";
    }
}

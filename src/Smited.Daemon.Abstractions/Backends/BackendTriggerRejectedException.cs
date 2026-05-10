using Smited.V1;

namespace Smited.Daemon.Backends;

/// <summary>
/// Thrown by an <see cref="IHapticBackend"/> implementation from
/// <see cref="IHapticBackend.TriggerAsync"/> to signal a per-instance
/// rejection that the daemon's generic
/// <c>ParameterSchema</c>-based validation can't catch. The
/// <c>TriggerCoordinator</c> catches this exception specifically and
/// maps it to a <c>TriggerResponse{accepted=false, error=...}</c>
/// preserving the original code, message, and field.
/// </summary>
/// <remarks>
/// <para>
/// Use cases the schema can't express:
/// </para>
/// <list type="bullet">
///   <item>Per-instance allow-lists (e.g. PiShock's per-shocker
///   <c>AllowedOps</c>: one shocker permits Shock, another doesn't).</item>
///   <item>Per-instance value caps that depend on cross-parameter
///   relationships (e.g. PiShock's per-op intensity ceilings).</item>
///   <item>Token-bucket rate limiting that exceeds what
///   <c>ConcurrencyModel</c> can express.</item>
/// </list>
/// <para>
/// Backends that simply play whatever the coordinator hands them (the
/// OWO mock and real backends today) never throw this — the coordinator's
/// schema validation is enough. New backends opt in only when their
/// per-instance constraints aren't expressible in the schema.
/// </para>
/// </remarks>
public sealed class BackendTriggerRejectedException : Exception
{
    /// <summary>Wire error code surfaced to the gRPC client.</summary>
    public TriggerErrorCode Code { get; }

    /// <summary>Optional path to the offending field, e.g. <c>microsensations[0].parameters.op</c>.</summary>
    public string? Field { get; }

    public BackendTriggerRejectedException(TriggerErrorCode code, string message, string? field = null)
        : base(message)
    {
        Code = code;
        Field = field;
    }
}

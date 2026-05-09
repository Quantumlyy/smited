using Smited.V1;

namespace Smited.Daemon.Triggering;

/// <summary>
/// Outcome of <see cref="TriggerCoordinator.TriggerAsync"/>. Domain
/// rejections come back as the success-shaped <see cref="Rejected"/>
/// variant, NOT as exceptions — the gRPC wire shape models them as
/// <c>TriggerResponse{accepted=false, error=...}</c>.
/// </summary>
internal abstract record TriggerOutcome(string ClientTraceId)
{
    public sealed record Accepted(string ClientTraceId, string SensationId)
        : TriggerOutcome(ClientTraceId);

    public sealed record Rejected(
        string ClientTraceId,
        TriggerErrorCode Code,
        string Message,
        string? Field) : TriggerOutcome(ClientTraceId);
}

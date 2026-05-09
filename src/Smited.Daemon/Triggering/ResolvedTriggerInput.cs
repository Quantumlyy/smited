using Smited.Daemon.Backends.Internal;

namespace Smited.Daemon.Triggering;

/// <summary>
/// Input the gRPC service hands to <see cref="TriggerCoordinator"/> after
/// parsing the wire <c>TriggerRequest</c>. Exactly one of
/// <see cref="SensationName"/> or <see cref="InlineMicrosensations"/>
/// must be non-null.
/// </summary>
internal sealed record ResolvedTriggerInput(
    string BackendId,
    string? SensationName,
    IReadOnlyList<MicrosensationParameters>? InlineMicrosensations,
    IReadOnlyList<string> ZoneIds,
    uint? IntensityScale,
    int Priority,
    string ClientTraceId);

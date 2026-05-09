namespace Smited.Daemon.Backends.Internal;

/// <summary>
/// Stop payload handed to a backend. The wire-level <c>StopRequest</c> oneof
/// includes a <c>backend_id</c> case the coordinator uses for routing; once
/// resolved to a specific backend, only the per-sensation vs. all-on-this-
/// backend distinction matters.
/// </summary>
public sealed record BackendStopRequest(
    string? SensationId,
    bool All);

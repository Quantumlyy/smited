namespace Smited.Daemon.Backends.Internal;

/// <summary>
/// What the backend reports back after accepting a trigger: the runtime
/// sensation id (echoed) and an estimate of how long the sensation will
/// run, used by the coordinator's tracking and by tests.
/// </summary>
public sealed record BackendTriggerResult(
    string SensationId,
    TimeSpan EstimatedDuration);

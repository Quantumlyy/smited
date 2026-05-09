using Smited.V1;

namespace Smited.Daemon.Backends.Internal;

/// <summary>
/// Internal lifecycle event emitted by backends and the registry. The gRPC
/// layer translates these to <c>smited.v1.Event</c> on the wire.
/// </summary>
public abstract record BackendEvent(string BackendId, DateTimeOffset Timestamp);

public sealed record SensationStarted(
    string BackendId,
    DateTimeOffset Timestamp,
    string SensationId,
    string? SensationName,
    string ClientTraceId) : BackendEvent(BackendId, Timestamp);

public sealed record SensationCompleted(
    string BackendId,
    DateTimeOffset Timestamp,
    string SensationId,
    string? SensationName,
    string ClientTraceId) : BackendEvent(BackendId, Timestamp);

public sealed record SensationCancelled(
    string BackendId,
    DateTimeOffset Timestamp,
    string SensationId,
    string? SensationName,
    string ClientTraceId,
    string? Reason) : BackendEvent(BackendId, Timestamp);

public sealed record BackendLifecycleEvent(
    string BackendId,
    DateTimeOffset Timestamp,
    BackendLifecycleChange Change,
    BackendSummarySnapshot Snapshot,
    string? Reason) : BackendEvent(BackendId, Timestamp);

public enum BackendLifecycleChange
{
    Registered,
    Deregistered,
    StatusChanged,
}

public sealed record CalibrationChangedEvent(
    string BackendId,
    DateTimeOffset Timestamp,
    CalibrationState NewState) : BackendEvent(BackendId, Timestamp);

public sealed record SensationRegistryChangedEvent(
    string BackendId,
    DateTimeOffset Timestamp,
    string SensationName,
    SensationRegistryChange Change) : BackendEvent(BackendId, Timestamp);

public enum SensationRegistryChange
{
    Registered,
    Unregistered,
}

/// <summary>
/// Headline backend metadata captured at event time. Backends can be
/// deregistered between event emit and event delivery; capturing a
/// snapshot makes the event payload self-contained.
/// </summary>
public sealed record BackendSummarySnapshot(
    string Id,
    string Kind,
    string DisplayName,
    BackendStatus Status,
    IReadOnlyList<string> Capabilities)
{
    public static BackendSummarySnapshot Of(Backends.IHapticBackend backend) =>
        new(backend.Id, backend.Kind, backend.DisplayName, backend.Status, backend.Capabilities.ToArray());
}

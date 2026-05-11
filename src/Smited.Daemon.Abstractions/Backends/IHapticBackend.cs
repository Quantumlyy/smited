using Google.Protobuf.WellKnownTypes;
using Smited.Daemon.Backends.Internal;
using Smited.Daemon.BodyMap;
using Smited.V1;

namespace Smited.Daemon.Backends;

/// <summary>
/// The central abstraction. Every backend implements this contract.
/// Static descriptors (zones, parameters, concurrency, calibration) reuse
/// proto types directly because they round-trip 1:1 to the wire.
/// Triggering and lifecycle flows use internal records so backends aren't
/// coupled to wire details (oneof shape, validation annotations).
/// </summary>
public interface IHapticBackend : IAsyncDisposable
{
    /// <summary>Unique runtime id assigned at registration, e.g. <c>"mock-owo"</c>.</summary>
    string Id { get; }

    /// <summary>Hardware family, e.g. <c>"owo_skin"</c>.</summary>
    string Kind { get; }

    string DisplayName { get; }

    BackendStatus Status { get; }

    IReadOnlyList<string> Capabilities { get; }

    ZoneTopology Zones { get; }

    ParameterSchema Parameters { get; }

    ConcurrencyModel Concurrency { get; }

    CalibrationState? Calibration { get; }

    Struct? Extras { get; }

    /// <summary>
    /// Body regions where this backend's hardware must never fire, per
    /// the manufacturer's safety guidance. Non-overridable: the
    /// bodymap validator refuses to register a backend whose declared
    /// placements land in these regions, regardless of user
    /// configuration. Backends with no manufacturer-stated bans return
    /// an empty set.
    /// </summary>
    IReadOnlySet<BodyRegion> ForbiddenRegions { get; }

    Task ConnectAsync(CancellationToken ct);

    Task<BackendTriggerResult> TriggerAsync(BackendTriggerRequest request, CancellationToken ct);

    Task<int> StopAsync(BackendStopRequest request, CancellationToken ct);

    /// <summary>
    /// Returns a short, low-intensity microsensation suitable for the
    /// admin body map's click-to-fire diagnostic. Parameter names are
    /// not standardized across backends (OWO has <c>frequency</c>;
    /// bHaptics doesn't; PiShock uses an <c>op</c> enum), so each
    /// backend supplies its own values that pass its own
    /// <see cref="Parameters"/> schema. Expected to be ~0.3s at
    /// ~50–60% intensity — noticeable but not startling, lets the
    /// operator identify which zone fired without playing back a full
    /// authored sensation.
    /// </summary>
    MicrosensationParameters BuildDiagnosticMicrosensation();

    /// <summary>
    /// Cold stream of lifecycle events (sensation started/completed/cancelled,
    /// status changes, calibration changes). The <c>EventBus</c> subscribes
    /// to this on backend registration and re-publishes for daemon-wide fan-out.
    /// </summary>
    IAsyncEnumerable<BackendEvent> Events { get; }
}

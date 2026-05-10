using Smited.V1;

namespace Smited.Daemon.Backends.Mock;

/// <summary>
/// Test-only control surface for <see cref="MockBhapticsBackend"/>. Public
/// on purpose: tests resolve it through the daemon's DI container without
/// needing <c>InternalsVisibleTo</c>. The mock backend implements both
/// <see cref="IHapticBackend"/> and this interface and the DI registration
/// binds the same instance to both service types. Mirrors the
/// <see cref="IMockOwoController"/> pattern.
/// </summary>
public interface IMockBhapticsController
{
    /// <summary>
    /// Snapshot of currently in-flight sensation ids on the mock. bHaptics'
    /// real concurrency model permits motor-summing overlap, so multiple
    /// entries may be present simultaneously.
    /// </summary>
    IReadOnlyCollection<string> ActiveSensationIds { get; }

    /// <summary>
    /// Toggles whether the mock advertises optional accessories
    /// (TactGloves, TactSleeves). Default <c>false</c>: the mock advertises
    /// a TactSuit X40 only. Tests use this to exercise the multi-device
    /// topology code path without changing fixture wiring. A
    /// <c>BackendLifecycleEvent</c> with
    /// <see cref="Smited.Daemon.Backends.Internal.BackendLifecycleChange.StatusChanged"/>
    /// is emitted so subscribers re-fetch the topology.
    /// </summary>
    void SetAccessoriesPresent(bool present);

    /// <summary>
    /// Emits a synthetic backend status-change event with the supplied
    /// status. Used by tests that exercise the event-bus → history pipeline
    /// for status transitions.
    /// </summary>
    void EmitStatusChange(BackendStatus newStatus, string? reason = null);
}

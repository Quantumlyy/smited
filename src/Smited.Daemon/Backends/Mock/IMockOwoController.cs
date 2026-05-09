namespace Smited.Daemon.Backends.Mock;

/// <summary>
/// Test-only control surface for <see cref="MockOwoBackend"/>. Public on
/// purpose: tests resolve it through the daemon's DI container, without
/// needing <c>InternalsVisibleTo</c>. The mock backend implements both
/// <see cref="IHapticBackend"/> and this interface and the DI registration
/// binds the same instance to both service types.
/// </summary>
public interface IMockOwoController
{
    /// <summary>
    /// Flips the mock's calibration state and emits a
    /// <c>CalibrationChanged</c> event so subscribers see the change.
    /// </summary>
    void SetCalibrated(bool calibrated, DateTimeOffset? at = null);

    /// <summary>Snapshot of currently in-flight sensation ids on the mock.</summary>
    IReadOnlyCollection<string> ActiveSensationIds { get; }
}

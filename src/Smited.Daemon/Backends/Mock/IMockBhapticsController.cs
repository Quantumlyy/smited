namespace Smited.Daemon.Backends.Mock;

/// <summary>
/// Test-only control surface for the mock bHaptics backends. Public on
/// purpose: tests resolve it through the daemon's DI container without
/// needing <c>InternalsVisibleTo</c>. Each mock backend
/// (<see cref="MockBhapticsVestBackend"/>,
/// <see cref="MockBhapticsSleeveBackend"/>,
/// <see cref="MockBhapticsFeetBackend"/>) implements both
/// <see cref="IHapticBackend"/> and this interface, and Program.cs
/// registers the same instance under both service types.
/// </summary>
/// <remarks>
/// <para>
/// Unlike <c>IMockOwoController</c>, this surface exposes
/// <see cref="RecentSubmissions"/> so tests can assert against the
/// exact motor byte arrays the mock received — the OWO mock has no
/// payload to inspect because OWO's SDK takes a different shape, but
/// for bHaptics the motor map is the load-bearing translation we
/// most want to pin in cross-platform tests.
/// </para>
/// </remarks>
public interface IMockBhapticsController
{
    /// <summary>
    /// Flips the mock's calibration state and emits a
    /// <c>CalibrationChanged</c> event so subscribers see the change.
    /// Mirrors <c>IMockOwoController.SetCalibrated</c>.
    /// </summary>
    void SetCalibrated(bool calibrated, DateTimeOffset? at = null);

    /// <summary>Snapshot of currently in-flight sensation ids on this
    /// mock.</summary>
    IReadOnlyCollection<string> ActiveSensationIds { get; }

    /// <summary>
    /// Snapshot of motor-payload submissions captured by this mock
    /// since boot or the last <see cref="ClearSubmissions"/> call.
    /// Capped at 100 entries (drop-oldest) so a long-running smoke
    /// session doesn't accumulate unbounded state. Each entry's
    /// <see cref="MockBhapticsSubmission.MotorIntensities"/> is an
    /// <see cref="System.Collections.Immutable.ImmutableArray{T}"/>
    /// so the buffer cannot be mutated across test cases.
    /// </summary>
    IReadOnlyList<MockBhapticsSubmission> RecentSubmissions { get; }

    /// <summary>Reset the recent-submissions buffer between
    /// independent test cases.</summary>
    void ClearSubmissions();
}

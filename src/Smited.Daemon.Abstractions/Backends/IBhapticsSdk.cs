namespace Smited.Daemon.Backends;

/// <summary>
/// Substitutable wrapper over the bHaptics Player WebSocket (the
/// <c>Bhaptics.Tac.HapticPlayer</c> SDK1 surface). One singleton fronts
/// every <c>bhaptics_*</c> backend kind — vest, sleeve_l, sleeve_r,
/// feet_l, feet_r — because the bHaptics Player is a single per-host
/// process that owns all paired devices.
/// </summary>
/// <remarks>
/// <para>
/// Lives in the abstractions assembly so the daemon host can register
/// an implementation cross-platform — the real <c>StaticBhapticsSdk</c>
/// impl is Windows-only and loaded reflectively, but fakes used by
/// tests can be wired anywhere. Method shapes use only primitive types
/// (and the smited-side <c>deviceKey</c> string) so test projects
/// targeting <c>net9.0</c> can substitute this interface without
/// taking a dependency on the <c>net9.0-windows</c> <c>Bhaptics.Tac</c>
/// package.
/// </para>
/// <para>
/// The bHaptics Player auto-reconnects to paired devices on its own
/// once it is running, so the daemon's per-backend heartbeat loop
/// only needs to observe per-device connectivity through
/// <see cref="IsDeviceConnected"/>; there is no equivalent of OWO's
/// "ConnectAsync(ip)" call.
/// </para>
/// </remarks>
public interface IBhapticsSdk : IAsyncDisposable
{
    /// <summary>
    /// Open the WebSocket connection to the bHaptics Player. Idempotent;
    /// subsequent calls are no-ops. Must be called before any
    /// <see cref="Submit"/> / <see cref="IsDeviceConnected"/> /
    /// <see cref="StopDevice"/> call. The SDK identity (AppId) is read
    /// from the daemon-global <c>BhapticsGlobalOptions</c> the
    /// implementation was constructed with, not from a per-call
    /// parameter — see <c>BhapticsGlobalOptions</c> for rationale.
    /// </summary>
    Task InitializeAsync(CancellationToken ct);

    /// <summary>
    /// Whether the bHaptics Player is currently running and reachable on
    /// its local WebSocket. Distinct from per-device connectivity (see
    /// <see cref="IsDeviceConnected"/>).
    /// </summary>
    bool IsPlayerRunning { get; }

    /// <summary>
    /// Whether the named device is paired and reporting active to the
    /// Player. The <paramref name="deviceKey"/> is smited's vocabulary
    /// (<c>"vest" | "sleeve_l" | "sleeve_r" | "feet_l" | "feet_r"</c>);
    /// the implementation maps these to <c>Bhaptics.Tac.PositionType</c>
    /// internally.
    /// </summary>
    bool IsDeviceConnected(string deviceKey);

    /// <summary>
    /// Submit a per-motor intensity array to a specific device. The
    /// Player handles the wall-clock timing for the supplied
    /// <paramref name="durationMs"/>; smited does not need to manage
    /// per-microsensation timing inside the SDK call.
    /// </summary>
    /// <param name="deviceKey">Device to target.</param>
    /// <param name="motorIntensities">Per-motor intensities 0..100; length
    /// must equal the device's motor count (40 vest, 6 sleeve, 3 feet).</param>
    /// <param name="durationMs">Active duration in milliseconds.</param>
    void Submit(string deviceKey, byte[] motorIntensities, int durationMs);

    /// <summary>
    /// Cancel any in-flight pattern on a specific device. The submission
    /// key the Player tracks internally is smited's <paramref name="deviceKey"/>,
    /// so this corresponds to <c>HapticPlayer.TurnOff(deviceKey)</c>.
    /// </summary>
    void StopDevice(string deviceKey);

    /// <summary>
    /// Cancel any in-flight pattern across every device the Player owns.
    /// Used by the daemon's panic endpoint and by
    /// <see cref="IAsyncDisposable.DisposeAsync"/>.
    /// </summary>
    void StopAll();
}

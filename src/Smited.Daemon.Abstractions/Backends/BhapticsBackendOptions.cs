namespace Smited.Daemon.Backends;

/// <summary>
/// Runtime configuration for the bHaptics backend. Lives in
/// <c>Smited.Daemon.Abstractions</c> so the platform-conditional
/// <c>Smited.Daemon.Bhaptics</c> assembly can take the type as a
/// constructor dependency without referencing the daemon host (which
/// would create a cycle in the build graph).
///
/// Bound from <c>Smited:Bhaptics</c> in <c>appsettings.json</c> via
/// <c>SmitedOptions.Bhaptics</c>; the daemon publishes this instance
/// as a DI singleton so <c>ActivatorUtilities.CreateInstance</c> can
/// hand it to the reflectively-loaded <c>BhapticsBackend</c>.
/// </summary>
public sealed class BhapticsBackendOptions
{
    /// <summary>
    /// Identity advertised to gRPC clients. Default
    /// <c>"bhaptics-primary"</c>; override only when running multiple
    /// bHaptics backends against distinct devices on a single host
    /// (rare — most setups have one suit and one Player).
    /// </summary>
    public string BackendId { get; set; } = "bhaptics-primary";

    /// <summary>
    /// WebSocket endpoint exposed by the local bHaptics Player.
    /// Default <c>ws://localhost:15881/v2/feedbacks</c>; override only
    /// if the Player's port has been customised.
    /// </summary>
    public string PlayerEndpoint { get; set; } = "ws://localhost:15881/v2/feedbacks";

    /// <summary>
    /// Reconnect attempts on Player disconnect. Default 3 attempts
    /// with exponential backoff (1s, 2s, 4s, ...). After the limit,
    /// the backend's <c>Status</c> transitions to
    /// <c>BACKEND_STATUS_ERROR</c> and a <c>BackendLifecycleEvent</c>
    /// is emitted. Set to <c>0</c> to skip reconnect entirely — the
    /// backend will go straight from <c>DISCONNECTED</c> to
    /// <c>ERROR</c> on the first drop.
    /// </summary>
    public int MaxReconnectAttempts { get; set; } = 3;

    /// <summary>
    /// How long <c>ConnectAsync</c> waits after the WebSocket handshake
    /// completes for the Player to push its first <c>deviceStatus</c>
    /// frame. The frame populates the live accessory set so
    /// <c>SensationLoader</c> validates persisted accessory-targeting
    /// sensations against the expanded topology rather than the
    /// vest-only default. Default 1500ms — enough for a healthy local
    /// Player, short enough that a slow or absent Player doesn't block
    /// daemon boot indefinitely. Set to <c>0</c> to skip the wait.
    /// </summary>
    public int InitialStatusTimeoutMillis { get; set; } = 1500;
}

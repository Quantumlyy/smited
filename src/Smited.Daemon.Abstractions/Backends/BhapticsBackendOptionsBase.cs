namespace Smited.Daemon.Backends;

/// <summary>
/// Shared per-instance configuration for every bHaptics backend kind
/// (vest, sleeve_l, sleeve_r, feet_l, feet_r). Concrete options classes
/// derive from this; today they are empty type-distinct binding targets,
/// kept so per-device fields can land in a derived class later without
/// breaking config bindings.
/// </summary>
/// <remarks>
/// <para>
/// Does NOT include an <c>AppId</c> field — AppId is daemon-global
/// (<see cref="BhapticsGlobalOptions"/>) because the SDK is a singleton
/// and its identity is decided at first init.
/// </para>
/// <para>
/// Does NOT include a <c>Side</c> field — left/right is intrinsic to
/// the backend's <c>Kind</c> discriminator
/// (<c>bhaptics_sleeve_l</c> vs <c>bhaptics_sleeve_r</c>) and is supplied
/// to the backend constructor by the factory.
/// </para>
/// <para>
/// Does NOT include a <c>ManualIp</c> field — the bHaptics Player only
/// listens on <c>localhost</c>; there is no LAN-routing equivalent of
/// OWO's MyOWO IP override.
/// </para>
/// </remarks>
public abstract class BhapticsBackendOptionsBase
{
    /// <summary>
    /// Identity advertised on <see cref="IHapticBackend.Id"/>. Concrete
    /// derivatives set a kind-appropriate default in their parameterless
    /// constructor (e.g. <c>"bhaptics-vest"</c>).
    /// </summary>
    public string BackendId { get; set; } = "";

    /// <summary>
    /// Reconnect attempts on per-device transport drop. The bHaptics
    /// Player auto-reconnects pairings on its own; this controls how
    /// long smited's backend keeps trying to re-observe the device as
    /// connected before flipping to <c>BackendStatus.Error</c>. Default 3.
    /// </summary>
    public int MaxReconnectAttempts { get; set; } = 3;

    /// <summary>
    /// Per-device connection-state poll period in seconds. Lower values
    /// detect device drops faster at the cost of more polling. Default 5.
    /// </summary>
    public int HeartbeatSeconds { get; set; } = 5;
}

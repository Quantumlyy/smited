namespace Smited.Daemon.Pishock;

/// <summary>
/// Per-descriptor PiShock backend options. Bound from
/// <c>Smited:Backends:Items:{i}:Options</c> by the factory.
/// </summary>
/// <remarks>
/// <para>
/// Defaults are deliberately conservative. <see cref="AllowedOps"/>
/// ships with <c>Vibrate</c> and <c>Beep</c> only — users opt into
/// <c>Shock</c> per shocker. <see cref="MaxIntensityShock"/> caps shock
/// intensity at 30% even when a sensation file authors a higher value,
/// because a bundled sensation library entry tuned for one user's
/// tolerance can otherwise land hard on another user's device.
/// </para>
/// <para>
/// Each instance is independent — <c>pishock</c> is not a singleton
/// kind. A user with two shockers configures two descriptors with
/// different ids and possibly different transports.
/// </para>
/// </remarks>
public sealed class PishockBackendOptions
{
    /// <summary>
    /// Transport selector. Defaults to <see cref="PishockTransportMode.Cloud"/>
    /// because cloud setup needs no LAN-side network configuration —
    /// users who paste credentials get a working backend, and LAN-mode
    /// users self-select by changing this field.
    /// </summary>
    public PishockTransportMode Mode { get; set; } = PishockTransportMode.Cloud;

    /// <summary>Cloud-mode: PiShock account username.</summary>
    public string? Username { get; set; }

    /// <summary>Cloud-mode: PiShock API key (per-account, generated in the PiShock account UI).</summary>
    public string? ApiKey { get; set; }

    /// <summary>Cloud-mode: per-shocker share code (generated in the PiShock account UI).</summary>
    public string? ShareCode { get; set; }

    /// <summary>LAN-mode: IPv4 address of the device on the local network.</summary>
    public string? DeviceIp { get; set; }

    /// <summary>LAN-mode: HTTP port on the device. Defaults to 80 in the factory when unset.</summary>
    public int? DevicePort { get; set; }

    /// <summary>
    /// Operations the daemon will forward to this device. A trigger
    /// requesting an op outside this list is rejected with
    /// <c>INVALID_PARAMETER</c> at trigger time. The default list excludes
    /// <see cref="PishockOp.Shock"/> — users opt in by adding it.
    /// </summary>
    /// <remarks>
    /// Nullable on purpose. .NET's configuration binder for
    /// <see cref="List{T}"/> appends bound items to the existing list
    /// rather than replacing it; with a non-null default,
    /// <c>AllowedOps: ["Shock"]</c> in config produces
    /// <c>[Vibrate, Beep, Shock]</c> and the user can't actually narrow
    /// the allow-list. Defaulting to <c>null</c> lets the binder
    /// allocate a fresh list, and <see cref="EffectiveAllowedOps"/>
    /// applies the default-on-null fallback at read time.
    /// </remarks>
    public List<PishockOp>? AllowedOps { get; set; }

    /// <summary>Default-on-null view of <see cref="AllowedOps"/>: Vibrate and Beep.</summary>
    public IReadOnlyList<PishockOp> EffectiveAllowedOps =>
        AllowedOps ?? DefaultAllowedOps;

    internal static readonly IReadOnlyList<PishockOp> DefaultAllowedOps =
        new[] { PishockOp.Vibrate, PishockOp.Beep };

    /// <summary>
    /// Hard ceiling on shock intensity (0..100) regardless of what the
    /// sensation file requested. Even users who flip on <c>Shock</c> in
    /// <see cref="AllowedOps"/> get a daemon-enforced cap. Default 30.
    /// </summary>
    public int MaxIntensityShock { get; set; } = 30;

    /// <summary>Hard ceiling on vibrate intensity (0..100). Default 100.</summary>
    public int MaxIntensityVibrate { get; set; } = 100;

    /// <summary>Hard ceiling on per-op duration in milliseconds. Default 1500ms.</summary>
    public int MaxDurationMs { get; set; } = 1500;

    /// <summary>Token-bucket refill rate in ops/second. Default 1.</summary>
    public int MaxOpsPerSecond { get; set; } = 1;

    /// <summary>
    /// Token-bucket capacity (the largest burst before rate-limiting
    /// kicks in). Default 3 — enough for a three-pulse pattern at the
    /// default refill rate.
    /// </summary>
    public int MaxBurst { get; set; } = 3;

    /// <summary>HTTP request timeout in milliseconds. Default 5000.</summary>
    public int RequestTimeoutMs { get; set; } = 5000;

    /// <summary>
    /// Optional human-readable display name for this shocker. Falls
    /// back to the descriptor id when unset.
    /// </summary>
    public string? DisplayName { get; set; }
}

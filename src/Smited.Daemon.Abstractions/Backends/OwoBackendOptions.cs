using System.Net;

namespace Smited.Daemon.Backends;

/// <summary>
/// Configuration for the real OWO Skin haptic backend
/// (<c>Smited.Daemon.Owo.OwoBackend</c>).
/// </summary>
/// <remarks>
/// Lives in the abstractions assembly so both the daemon host (which
/// exposes it via the <c>Smited.Daemon.Configuration.SmitedOptions</c>
/// tree) and the platform-conditional OWO backend project can reference
/// the type without forcing a Mac-side ProjectReference to the Windows
/// backend assembly. This file compiles into the abstractions assembly
/// on every host; the OWO project's SDK-touching files
/// (<c>OwoBackend.cs</c>, <c>StaticOwoSdk.cs</c>, <c>OwoMuscleMap.cs</c>)
/// are gated on the <c>_TargetingWindows</c> MSBuild property defined
/// in <c>Directory.Build.props</c>, which evaluates true when either
/// the host is Windows or the build was given a <c>win-*</c>
/// <c>RuntimeIdentifier</c> (i.e. cross-publish). Their bodies are
/// additionally wrapped in <c>#if WINDOWS</c> for IDE clarity. See
/// <c>docs/adding-a-backend.md</c> for the full conditional-reference
/// pattern any new platform-specific backend should follow.
/// </remarks>
public sealed class OwoBackendOptions
{
    /// <summary>
    /// Identity advertised on <see cref="IHapticBackend.Id"/>. Defaults to
    /// <c>"owo-primary"</c>; multi-OWO setups can disambiguate by tagging
    /// additional instances.
    /// </summary>
    public string BackendId { get; set; } = "owo-primary";

    /// <summary>
    /// Display name registered with the MyOWO app's "Scan Games" panel.
    /// </summary>
    public string GameDisplayName { get; set; } = "smited haptic daemon";

    /// <summary>
    /// Optional manually-specified IP for the MyOWO app, bypassing
    /// auto-discovery. Set when MyOWO runs on a different machine on the
    /// LAN, or when auto-discovery fails on the network. <c>null</c> or
    /// empty means use auto-discovery.
    /// </summary>
    public string? ManualIp { get; set; }

    /// <summary>
    /// Reconnect attempts on transport drop. Each attempt is gated by
    /// exponential backoff (2^attempt seconds). After exhaustion the
    /// backend transitions to <c>BackendStatus.Error</c> and a manual
    /// daemon restart is required to retry. Default 3.
    /// </summary>
    public int MaxReconnectAttempts { get; set; } = 3;

    /// <summary>
    /// Connection-state poll period in seconds. Lower values detect
    /// transport drops faster at the cost of more polling work. Default 5.
    /// </summary>
    public int HeartbeatSeconds { get; set; } = 5;

    /// <summary>
    /// Maximum time the daemon waits for the OWO SDK's connect handshake
    /// to complete before declaring the backend
    /// <c>BackendStatus.Disconnected</c> and continuing daemon startup.
    /// The heartbeat loop continues attempting reconnect in the background;
    /// the backend may transition to <c>BackendStatus.Ready</c> later
    /// without a daemon restart. Default 10 seconds.
    /// </summary>
    /// <remarks>
    /// Set to 0 to disable the deadline (block startup until the SDK
    /// responds — the pre-fix behavior). Useful for headless test fixtures
    /// with a known-responsive mock SDK.
    /// </remarks>
    public int ConnectTimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// True when <see cref="ManualIp"/> is unset (use auto-connect) or
    /// is a parseable IPv4/IPv6 address.
    /// </summary>
    public bool IsManualIpValid() =>
        string.IsNullOrEmpty(ManualIp) || IPAddress.TryParse(ManualIp, out _);
}

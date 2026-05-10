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
/// backend assembly. Mac builds compile this file as part of the
/// abstractions assembly; only the OWO project's <c>OwoBackend.cs</c>
/// is gated behind <c>$(OS) == Windows_NT</c>.
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
    /// True when <see cref="ManualIp"/> is unset (use auto-connect) or
    /// is a parseable IPv4/IPv6 address.
    /// </summary>
    public bool IsManualIpValid() =>
        string.IsNullOrEmpty(ManualIp) || IPAddress.TryParse(ManualIp, out _);
}

namespace Smited.Daemon.Pishock;

/// <summary>
/// Transport for one PiShock device. Implementations encapsulate
/// either the cloud API (HTTPS to <c>do.pishock.com</c>) or the LAN
/// API (HTTP to a local device IP). The real backend depends on this
/// abstraction rather than calling <c>HttpClient</c> directly so tests
/// can stub the wire without spinning up a server.
/// </summary>
public interface IPishockClient
{
    /// <summary>
    /// Fires one op against the device. Returns a <see cref="PishockOpResult"/>
    /// rather than throwing on transport failure so the caller can
    /// translate "the device said no" into a structured rejection on
    /// the gRPC wire instead of an unhelpful Internal error code.
    /// </summary>
    Task<PishockOpResult> SendOpAsync(
        PishockOp op,
        int durationMs,
        int intensity,
        CancellationToken ct);
}

/// <summary>
/// Outcome of one op submitted via <see cref="IPishockClient.SendOpAsync"/>.
/// <see cref="Accepted"/> distinguishes "device said it ran the op" from
/// "device or network said no"; <see cref="RawResponse"/> carries the
/// device's plaintext reply for debugging.
/// </summary>
public sealed record PishockOpResult(
    bool Accepted,
    string? RawResponse,
    string? ErrorMessage);

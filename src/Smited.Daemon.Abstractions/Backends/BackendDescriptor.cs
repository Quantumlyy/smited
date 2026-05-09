using Google.Protobuf.WellKnownTypes;
using Smited.V1;

namespace Smited.Daemon.Backends;

/// <summary>
/// Atomic snapshot of a backend's full self-description, used by tests and
/// the gRPC <c>DescribeBackend</c> handler. Built on demand from an
/// <see cref="IHapticBackend"/>.
/// </summary>
public sealed record BackendDescriptor(
    string Id,
    string Kind,
    string DisplayName,
    BackendStatus Status,
    IReadOnlyList<string> Capabilities,
    ZoneTopology Zones,
    ParameterSchema Parameters,
    ConcurrencyModel Concurrency,
    CalibrationState? Calibration,
    Struct? Extras);

public static class BackendDescriptorExtensions
{
    public static BackendDescriptor Snapshot(this IHapticBackend backend) =>
        new(
            backend.Id,
            backend.Kind,
            backend.DisplayName,
            backend.Status,
            backend.Capabilities,
            backend.Zones,
            backend.Parameters,
            backend.Concurrency,
            backend.Calibration,
            backend.Extras);
}

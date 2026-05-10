using Smited.V1;

namespace Smited.Daemon.Events;

/// <summary>
/// gRPC <c>SubscribeEventsRequest</c> filters, normalised to set lookups.
/// Empty sets mean "no filter". Filtering happens on the subscriber side
/// so adding a filter doesn't require bus reconfiguration.
/// </summary>
internal sealed record SubscribeFilters(
    IReadOnlySet<EventKind> Kinds,
    IReadOnlySet<string> BackendIds)
{
    public static SubscribeFilters None { get; } = new(
        new HashSet<EventKind>(),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase));
}

using System.Text.RegularExpressions;

namespace Smited.Daemon.Configuration;

/// <summary>
/// Validates the user-supplied list of <see cref="BackendDescriptor"/>
/// entries before <c>BackendBootstrapper</c> dispatches them to factories.
/// Configuration mistakes (empty kind, duplicate id, malformed id) abort
/// startup with a clear list of every problem so the user can fix them
/// in one pass instead of one error per restart.
/// </summary>
internal static partial class BackendDescriptorValidator
{
    [GeneratedRegex("^[a-z0-9][a-z0-9_-]*$")]
    private static partial Regex IdPattern();

    /// <summary>
    /// Backend kinds whose factories share state across instances —
    /// either a DI singleton backend object, a static SDK, or a single
    /// connection to a piece of physical hardware that two backends
    /// would fight over. Two descriptors of the same kind would
    /// silently corrupt each other's state, so the validator rejects
    /// the configuration up-front. Add new kinds here whenever a
    /// factory's underlying SDK or shared resource can't safely host
    /// multiple instances; see <c>IBackendFactory</c> remarks for the
    /// full criterion.
    /// </summary>
    private static readonly IReadOnlySet<string> SingletonKinds =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // MockOwoBackend is a DI singleton (IMockOwoController is
            // wired to the same instance) so two descriptors would
            // overwrite each other's Id / DisplayName overrides.
            "mock_owo",
            // OwoBackend depends on a static OWOGame.OWO SDK and the
            // single MyOWO connection it owns; two instances would
            // race on Send / Stop and the device would fire whatever
            // the most-recent caller asked for.
            "owo_skin",
        };

    /// <summary>
    /// Returns a list of human-readable validation errors. Empty list
    /// means the descriptors are well-formed; a non-empty list should
    /// abort startup so the user sees every problem at once.
    /// </summary>
    public static IReadOnlyList<string> Validate(IReadOnlyList<BackendDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);

        var errors = new List<string>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kindCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var firstSingletonOffenseIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < descriptors.Count; i++)
        {
            var d = descriptors[i];
            var prefix = $"Smited:Backends:Items[{i}]";

            if (string.IsNullOrWhiteSpace(d.Kind))
            {
                errors.Add($"{prefix}: Kind is required.");
            }

            if (string.IsNullOrWhiteSpace(d.Id))
            {
                errors.Add($"{prefix}: Id is required.");
            }
            else if (!IdPattern().IsMatch(d.Id))
            {
                errors.Add(
                    $"{prefix}: Id '{d.Id}' is not a valid identifier (must match ^[a-z0-9][a-z0-9_-]*$).");
            }
            else if (!seenIds.Add(d.Id))
            {
                errors.Add(
                    $"{prefix}: Id '{d.Id}' is duplicated; every descriptor must have a unique Id.");
            }

            if (!string.IsNullOrWhiteSpace(d.Kind) && SingletonKinds.Contains(d.Kind))
            {
                kindCounts.TryGetValue(d.Kind, out var seenCount);
                kindCounts[d.Kind] = seenCount + 1;
                if (seenCount == 0)
                {
                    firstSingletonOffenseIndex[d.Kind] = i;
                }
                else if (seenCount == 1)
                {
                    var firstIndex = firstSingletonOffenseIndex[d.Kind];
                    errors.Add(
                        $"Smited:Backends:Items[{firstIndex},{i}]: Kind '{d.Kind}' may appear at most once. "
                        + "The kind's factory shares state across instances (singleton backend object, "
                        + "static SDK, or single hardware connection) so two descriptors would corrupt each "
                        + "other's state. Configure additional instances using a different Kind.");
                }
                // For 3+ duplicates of the same singleton kind we already
                // emitted one error citing the first two indices; further
                // offenses are obvious from that message.
            }
        }

        return errors;
    }
}

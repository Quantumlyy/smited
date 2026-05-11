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
    /// Backend kinds whose factories share runtime state that prevents
    /// safe multi-instance operation — a DI singleton backend object,
    /// a static SDK, or a single connection to a piece of physical
    /// hardware that two backends would fight over.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The "at most one" check applies <strong>only to enabled
    /// descriptors</strong> of these kinds. Disabled descriptors never
    /// reach the bootstrapper's factory loop, so they don't conflict
    /// with the runtime singleton state and shouldn't fail validation.
    /// This is the documented "keep disconnected hardware config
    /// around disabled" workflow that <c>appsettings.Development.json</c>
    /// itself uses.
    /// </para>
    /// <para>
    /// Other validation rules (id uniqueness, kind / id well-formedness)
    /// apply to every descriptor regardless of <c>Enabled</c>, because
    /// those rules guard against config-shape ambiguity that doesn't
    /// depend on whether the entry is currently active. The runtime-
    /// invariant vs config-invariant distinction is the question to
    /// ask whenever adding a new validation rule: count what reaches
    /// runtime if the rule guards a runtime invariant; count
    /// everything otherwise.
    /// </para>
    /// </remarks>
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
            // Each bHaptics device is a singleton: one physical
            // TactSuit X40, one left TactSleeve, one right TactSleeve,
            // one left Tactosy for Feet, one right Tactosy for Feet.
            // Two descriptors of the same bhaptics_* kind would
            // compete for the same physical device's Submit / TurnOff
            // routing through the shared HapticPlayer.
            "bhaptics_vest",
            "bhaptics_sleeve_l",
            "bhaptics_sleeve_r",
            "bhaptics_feet_l",
            "bhaptics_feet_r",
            // Mock bhaptics backends are DI singletons (vest plain, sleeve
            // and feet keyed by side). Two descriptors of the same mock
            // kind would clobber each other's Id / DisplayName overrides
            // the same way two mock_owo descriptors would.
            "mock_bhaptics_vest",
            "mock_bhaptics_sleeve_l",
            "mock_bhaptics_sleeve_r",
            "mock_bhaptics_feet_l",
            "mock_bhaptics_feet_r",
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

            // Singleton-kind enforcement: count only ENABLED descriptors.
            // A disabled descriptor of a singleton kind never reaches
            // the factory loop, so it can't collide with the runtime
            // singleton state — it's legitimate config the operator
            // keeps around for fast toggling or documentation.
            if (d.Enabled
                && !string.IsNullOrWhiteSpace(d.Kind)
                && SingletonKinds.Contains(d.Kind))
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
                        $"Smited:Backends:Items[{firstIndex},{i}]: Kind '{d.Kind}' may appear at most once "
                        + "as an enabled descriptor. The kind's factory shares state across instances "
                        + "(singleton backend object, static SDK, or single hardware connection) so two "
                        + "active descriptors would corrupt each other's state. Configure additional "
                        + "instances using a different Kind, or set Enabled: false on the extra entries "
                        + "to keep them in the config without registering.");
                }
                // For 3+ enabled duplicates of the same singleton kind
                // we already emitted one error citing the first two
                // indices; further offenses are obvious from that
                // message.
            }
        }

        return errors;
    }
}

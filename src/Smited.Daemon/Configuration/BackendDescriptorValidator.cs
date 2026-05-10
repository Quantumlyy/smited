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
    /// Returns a list of human-readable validation errors. Empty list
    /// means the descriptors are well-formed; a non-empty list should
    /// abort startup so the user sees every problem at once.
    /// </summary>
    public static IReadOnlyList<string> Validate(IReadOnlyList<BackendDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);

        var errors = new List<string>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mockOwoCount = 0;

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

            if (string.Equals(d.Kind, "mock_owo", StringComparison.OrdinalIgnoreCase))
            {
                mockOwoCount++;
                if (mockOwoCount > 1)
                {
                    errors.Add(
                        $"{prefix}: Kind 'mock_owo' may appear at most once "
                        + "(MockOwoBackend is a DI singleton). Configure additional "
                        + "instances using a different Kind.");
                }
            }
        }

        return errors;
    }
}

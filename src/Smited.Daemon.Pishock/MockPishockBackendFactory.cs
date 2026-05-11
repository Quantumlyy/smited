using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Smited.Daemon.Backends;
using Smited.Daemon.Configuration;

namespace Smited.Daemon.Pishock;

/// <summary>
/// Factory for the in-process <see cref="MockPishockBackend"/>. Each
/// descriptor produces a fresh backend instance — unlike the OWO mock,
/// PiShock is multi-instance, so two descriptors never share state.
/// </summary>
/// <remarks>
/// <para>
/// Validation that surfaces as <see cref="BackendConfigurationException"/>:
/// </para>
/// <list type="bullet">
///   <item><c>AllowedOps</c> must be non-empty (a shocker that can't do anything is misconfiguration).</item>
///   <item><c>MaxIntensityShock</c> and <c>MaxIntensityVibrate</c> must be in <c>[0, 100]</c>.</item>
///   <item><c>MaxDurationMs</c>, <c>MaxOpsPerSecond</c>, and <c>MaxBurst</c> must be at least 1.</item>
///   <item><c>RequestTimeoutMs</c> must be at least 1.</item>
/// </list>
/// </remarks>
public sealed class MockPishockBackendFactory : IBackendFactory
{
    public string Kind => "mock_pishock";

    public IHapticBackend? TryCreate(
        BackendDescriptor descriptor,
        IConfigurationSection optionsSection,
        IServiceProvider services,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(optionsSection);
        ArgumentNullException.ThrowIfNull(services);

        // Validate the RAW AllowedOps entries before binding. The
        // configuration binder turns numeric JSON entries into enum
        // members by underlying value, so "AllowedOps": [0] would
        // silently opt the descriptor into PishockOp.Shock — bypassing
        // the named opt-in story documented for the Shock op. Reject
        // anything that isn't a recognized name first.
        ValidateAllowedOpsRawEntries(descriptor, optionsSection);

        var options = optionsSection.Get<PishockBackendOptions>() ?? new PishockBackendOptions();
        NormalizeExplicitEmptyAllowedOps(options, optionsSection);

        // The descriptor's top-level DisplayName is the documented
        // override surface; honor it over Options.DisplayName so the
        // sample config in docs/pishock-config-example.json works as
        // shown.
        if (!string.IsNullOrEmpty(descriptor.DisplayName))
        {
            options.DisplayName = descriptor.DisplayName;
        }

        ValidateOptions(descriptor, options);

        return ActivatorUtilities.CreateInstance<MockPishockBackend>(services, descriptor.Id, options);
    }

    /// <summary>
    /// Distinguishes "AllowedOps key absent in config" from "AllowedOps
    /// set to an empty array" — the configuration binder collapses both
    /// to <c>null</c>, so without this normalization an operator writing
    /// <c>"AllowedOps": []</c> in JSON to mean "refuse to start; this
    /// shocker fires nothing" would silently get the
    /// <c>[Vibrate, Beep]</c> default applied by
    /// <c>EffectiveAllowedOps</c>. Sets <see cref="PishockBackendOptions.AllowedOps"/>
    /// to an empty list when the config explicitly empty-arrayed it, so
    /// <see cref="ValidateOptions"/>'s <c>{ Count: 0 }</c> guard fires.
    /// </summary>
    /// <remarks>
    /// Shared between the mock and real factories; both have the same
    /// vulnerability since both Get&lt;PishockBackendOptions&gt;() on
    /// the same JSON.
    /// </remarks>
    /// <summary>
    /// Rejects raw <c>AllowedOps</c> entries that aren't recognized
    /// PiShock op names. <c>System.Enum.TryParse</c> and the
    /// configuration binder both accept numeric strings and map them to
    /// the corresponding enum member, so a JSON entry like <c>[0]</c>
    /// binds to <c>PishockOp.Shock</c> and silently bypasses the
    /// "Shock must be explicitly opted in by name" story documented
    /// across the user-facing surfaces. Walk the raw section's
    /// children and require each value to match one of the
    /// <c>vibrate</c> / <c>beep</c> / <c>shock</c> names.
    /// </summary>
    /// <remarks>
    /// Shared between mock and real PiShock factories. Same defense
    /// pattern as the smoke tool's <c>TryParseEnumName</c>, applied at
    /// the config-binding layer instead of CLI parsing.
    /// </remarks>
    internal static void ValidateAllowedOpsRawEntries(
        BackendDescriptor descriptor, IConfigurationSection optionsSection)
    {
        var section = optionsSection.GetSection("AllowedOps");
        var validNames = System.Enum.GetNames<PishockOp>();
        foreach (var child in section.GetChildren())
        {
            var raw = child.Value;
            if (string.IsNullOrEmpty(raw))
            {
                throw new BackendConfigurationException(descriptor.Id, descriptor.Kind,
                    $"AllowedOps[{child.Key}] is empty; entries must be one of "
                    + $"{string.Join(", ", validNames)} (case-insensitive name strings, "
                    + "not numeric values).");
            }
            var match = validNames.FirstOrDefault(n =>
                string.Equals(n, raw, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                throw new BackendConfigurationException(descriptor.Id, descriptor.Kind,
                    $"AllowedOps[{child.Key}]='{raw}' is not a recognized PiShock op. "
                    + $"Valid values: {string.Join(", ", validNames)} (named strings only — "
                    + "numeric values like 0 or 1 are rejected so Shock can't be opted "
                    + "into accidentally via PishockOp's underlying value).");
            }
        }
    }

    internal static void NormalizeExplicitEmptyAllowedOps(
        PishockBackendOptions options, IConfigurationSection optionsSection)
    {
        if (options.AllowedOps is not null)
        {
            return;
        }
        // The JSON provider creates a section at the AllowedOps path
        // even when the array is empty. Section.Exists() returns false
        // for both "missing" and "[]" (no value, no children), so we
        // check the parent's children explicitly for the key.
        var keyPresent = optionsSection.GetChildren().Any(c =>
            string.Equals(c.Key, "AllowedOps", StringComparison.OrdinalIgnoreCase));
        if (!keyPresent)
        {
            return;
        }
        var section = optionsSection.GetSection("AllowedOps");
        if (!section.GetChildren().Any() && section.Value is null)
        {
            options.AllowedOps = new List<PishockOp>();
        }
    }

    /// <summary>
    /// Validates a bound <see cref="PishockBackendOptions"/> against the
    /// descriptor and throws <see cref="BackendConfigurationException"/>
    /// on the first violation. Public so tests can drive validation
    /// directly without setting up an <c>IConfigurationSection</c>.
    /// </summary>
    public static void ValidateOptions(BackendDescriptor descriptor, PishockBackendOptions options)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(options);

        // Distinguish null (user didn't set AllowedOps; use defaults) from
        // explicit empty []. An empty list is misconfiguration — a
        // shocker that can't do anything is never what the user meant.
        if (options.AllowedOps is { Count: 0 })
        {
            throw new BackendConfigurationException(descriptor.Id, descriptor.Kind,
                "AllowedOps must contain at least one operation. A PiShock descriptor with no "
                + "allowed ops cannot fire anything; either remove the descriptor, omit "
                + "AllowedOps to take the default [Vibrate, Beep], or include at least one of "
                + "Vibrate, Beep, or Shock.");
        }

        if (options.MaxIntensityShock < 0 || options.MaxIntensityShock > 100)
        {
            throw new BackendConfigurationException(descriptor.Id, descriptor.Kind,
                $"MaxIntensityShock={options.MaxIntensityShock} is out of range; must be 0..100.");
        }
        if (options.MaxIntensityVibrate < 0 || options.MaxIntensityVibrate > 100)
        {
            throw new BackendConfigurationException(descriptor.Id, descriptor.Kind,
                $"MaxIntensityVibrate={options.MaxIntensityVibrate} is out of range; must be 0..100.");
        }
        if (options.MaxDurationMs <= 0)
        {
            throw new BackendConfigurationException(descriptor.Id, descriptor.Kind,
                $"MaxDurationMs={options.MaxDurationMs} must be positive.");
        }
        if (options.MaxOpsPerSecond <= 0)
        {
            throw new BackendConfigurationException(descriptor.Id, descriptor.Kind,
                $"MaxOpsPerSecond={options.MaxOpsPerSecond} must be at least 1.");
        }
        if (options.MaxBurst <= 0)
        {
            throw new BackendConfigurationException(descriptor.Id, descriptor.Kind,
                $"MaxBurst={options.MaxBurst} must be at least 1 (the bucket needs capacity for "
                + "at least one in-flight op).");
        }
        if (options.RequestTimeoutMs <= 0)
        {
            throw new BackendConfigurationException(descriptor.Id, descriptor.Kind,
                $"RequestTimeoutMs={options.RequestTimeoutMs} must be positive.");
        }
    }
}

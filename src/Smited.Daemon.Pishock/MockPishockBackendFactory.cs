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

        var options = optionsSection.Get<PishockBackendOptions>() ?? new PishockBackendOptions();
        ValidateOptions(descriptor, options);

        return ActivatorUtilities.CreateInstance<MockPishockBackend>(services, descriptor.Id, options);
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

        if (options.AllowedOps is null || options.AllowedOps.Count == 0)
        {
            throw new BackendConfigurationException(descriptor.Id, descriptor.Kind,
                "AllowedOps must contain at least one operation. A PiShock descriptor with no "
                + "allowed ops cannot fire anything; either remove the descriptor or include "
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

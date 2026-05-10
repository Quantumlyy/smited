using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Smited.Daemon.Backends;
using Smited.Daemon.Configuration;

namespace Smited.Daemon.Bhaptics;

/// <summary>
/// Factory for the real <see cref="BhapticsBackend"/>. Binds the
/// descriptor's <c>Options</c> sub-section to a fresh
/// <see cref="BhapticsBackendOptions"/>, applies the descriptor's
/// <see cref="BackendDescriptor.Id"/> as a
/// <see cref="BhapticsBackendOptions.BackendId"/> override, then
/// constructs the backend via
/// <see cref="ActivatorUtilities.CreateInstance{T}(IServiceProvider, object[])"/>.
/// </summary>
/// <remarks>
/// <para>
/// Public so the daemon host's
/// <c>BackendsServiceCollectionExtensions.AddSmitedBackends</c> can
/// reference and register the type directly. Unlike the OWO factory —
/// which is in a Windows-only assembly loaded reflectively — bHaptics
/// is a cross-platform <c>net9.0</c> assembly that the daemon links
/// against at compile time. The Windows-only constraint is at
/// <em>runtime</em>: bHaptics Player is Windows-only, so this factory
/// returns <c>null</c> on non-Windows hosts and lets
/// <c>BackendBootstrapper</c> log-and-skip with a clear message.
/// </para>
/// <para>
/// Multiple <c>bhaptics_tactsuit</c> descriptors are forbidden by the
/// descriptor validator: the bHaptics Player exposes a single local
/// WebSocket endpoint and only one backend can hold the connection.
/// </para>
/// </remarks>
public sealed class BhapticsBackendFactory : IBackendFactory
{
    /// <inheritdoc />
    public string Kind => "bhaptics_tactsuit";

    /// <inheritdoc />
    public IHapticBackend? TryCreate(
        BackendDescriptor descriptor,
        IConfigurationSection optionsSection,
        IServiceProvider services,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(optionsSection);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(logger);

        if (!OperatingSystem.IsWindows())
        {
            logger.LogInformation(
                "bhaptics_tactsuit factory declining to instantiate on non-Windows host: "
                + "bHaptics Player only runs on Windows.");
            return null;
        }

        // Bind the descriptor's Options sub-section. Get<T>() returns
        // null when the user omitted Options entirely; fall through
        // to defaults in that case.
        var options = optionsSection.Get<BhapticsBackendOptions>() ?? new BhapticsBackendOptions();

        // Descriptor-level Id wins over Options.BackendId so users can
        // change instance identity without rewriting the nested section.
        if (!string.IsNullOrEmpty(descriptor.Id))
        {
            options.BackendId = descriptor.Id;
        }

        var backend = ActivatorUtilities.CreateInstance<BhapticsBackend>(services, options);

        if (!string.IsNullOrEmpty(descriptor.DisplayName))
        {
            backend.OverrideDisplayName(descriptor.DisplayName);
        }

        return backend;
    }
}

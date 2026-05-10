using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Smited.Daemon.Configuration;

namespace Smited.Daemon.Backends.Mock;

/// <summary>
/// Factory for the in-process <see cref="MockBhapticsBackend"/>. Resolves
/// the singleton from DI so <c>IMockBhapticsController</c> stays wired
/// to the same instance, and applies any per-descriptor identity
/// overrides supplied by the user.
/// </summary>
/// <remarks>
/// Multiple <c>mock_bhaptics</c> descriptors are forbidden by the
/// descriptor validator: the backend is a DI singleton, and a second
/// <c>TryCreate</c> call with a conflicting <c>Id</c> or
/// <c>DisplayName</c> on the same instance would clobber the first
/// override and confuse downstream consumers (history, banner, gRPC
/// routing). The validator surfaces a clear configuration error before
/// this factory runs.
/// </remarks>
internal sealed class MockBhapticsBackendFactory : IBackendFactory
{
    public string Kind => "mock_bhaptics";

    public IHapticBackend? TryCreate(
        BackendDescriptor descriptor,
        IConfigurationSection optionsSection,
        IServiceProvider services,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(services);

        var instance = services.GetRequiredService<MockBhapticsBackend>();

        if (!string.IsNullOrEmpty(descriptor.Id))
        {
            instance.OverrideId(descriptor.Id);
        }
        if (!string.IsNullOrEmpty(descriptor.DisplayName))
        {
            instance.OverrideDisplayName(descriptor.DisplayName);
        }

        return instance;
    }
}

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Smited.Daemon.Configuration;

namespace Smited.Daemon.Backends.Mock;

/// <summary>
/// Factory for the cross-platform mock bHaptics backends. Five
/// effective mock kinds — <c>mock_bhaptics_vest</c>,
/// <c>mock_bhaptics_sleeve_l/r</c>, <c>mock_bhaptics_feet_l/r</c> —
/// dispatch through this one class.
/// </summary>
/// <remarks>
/// Same registration shape as <c>BhapticsBackendFactory</c>:
/// constructor takes the kind as a string, registration in
/// <see cref="BackendsServiceCollectionExtensions.AddSmitedBackends"/>
/// creates five instances. Internal because it lives in the daemon
/// host assembly (test access goes through
/// <see cref="IMockBhapticsController"/> resolved from DI).
/// </remarks>
internal sealed class MockBhapticsBackendFactory : IBackendFactory
{
    private readonly string _kind;

    public MockBhapticsBackendFactory(string kind)
    {
        _kind = kind;
    }

    public string Kind => _kind;

    public IHapticBackend? TryCreate(
        BackendDescriptor descriptor,
        IConfigurationSection optionsSection,
        IServiceProvider services,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(services);

        MockBhapticsBackendBase instance = _kind switch
        {
            "mock_bhaptics_vest" => services.GetRequiredService<MockBhapticsVestBackend>(),
            "mock_bhaptics_sleeve_l" => services.GetRequiredKeyedService<MockBhapticsSleeveBackend>("left"),
            "mock_bhaptics_sleeve_r" => services.GetRequiredKeyedService<MockBhapticsSleeveBackend>("right"),
            "mock_bhaptics_feet_l" => services.GetRequiredKeyedService<MockBhapticsFeetBackend>("left"),
            "mock_bhaptics_feet_r" => services.GetRequiredKeyedService<MockBhapticsFeetBackend>("right"),
            _ => throw new InvalidOperationException(
                $"MockBhapticsBackendFactory was constructed with unrecognised kind '{_kind}'"),
        };

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

    /// <summary>
    /// The five mock bHaptics kinds, in the order
    /// <see cref="BackendsServiceCollectionExtensions.AddSmitedBackends"/>
    /// registers them.
    /// </summary>
    internal static IReadOnlyList<string> SupportedKinds { get; } = new[]
    {
        "mock_bhaptics_vest",
        "mock_bhaptics_sleeve_l",
        "mock_bhaptics_sleeve_r",
        "mock_bhaptics_feet_l",
        "mock_bhaptics_feet_r",
    };
}

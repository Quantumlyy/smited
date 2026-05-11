// Excluded from compile on non-Windows hosts via the conditional
// <Compile Remove="BhapticsBackendFactory.cs"/> ItemGroup in
// Smited.Daemon.Bhaptics.csproj. The body is additionally guarded by
// `#if WINDOWS` for IDE clarity.

#if WINDOWS
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Smited.Daemon.Backends;
using Smited.Daemon.Configuration;

namespace Smited.Daemon.Bhaptics;

/// <summary>
/// Factory for the real bHaptics backends. Five effective kinds —
/// <c>bhaptics_vest</c>, <c>bhaptics_sleeve_l</c>,
/// <c>bhaptics_sleeve_r</c>, <c>bhaptics_feet_l</c>,
/// <c>bhaptics_feet_r</c> — dispatch through this one class because
/// <see cref="IBackendFactory.Kind"/> is a single string per
/// registration. <see cref="AddBhapticsBackendIfWindows"/> registers
/// this type five times in DI, once per kind constant, each instance
/// constructed via <see cref="ActivatorUtilities.CreateInstance"/>
/// with a different <c>kind</c> ctor argument.
/// </summary>
/// <remarks>
/// <para>
/// Public so the daemon host's
/// <c>BackendsServiceCollectionExtensions</c> can reflectively load it
/// via <c>Type.GetType</c> on Windows hosts — the daemon host does
/// not have a compile-time dependency on
/// <c>Smited.Daemon.Bhaptics</c>, so the type and its constructor
/// must be public for cross-assembly instantiation to work.
/// </para>
/// </remarks>
public sealed class BhapticsBackendFactory : IBackendFactory
{
    private readonly string _kind;
    private readonly IBhapticsSdk _sdk;
    private readonly TimeProvider _time;
    private readonly ILoggerFactory _loggerFactory;

    public BhapticsBackendFactory(
        string kind,
        IBhapticsSdk sdk,
        TimeProvider time,
        ILoggerFactory loggerFactory)
    {
        _kind = kind;
        _sdk = sdk;
        _time = time;
        _loggerFactory = loggerFactory;
    }

    /// <inheritdoc />
    public string Kind => _kind;

    /// <inheritdoc />
    public IHapticBackend? TryCreate(
        BackendDescriptor descriptor,
        IConfigurationSection optionsSection,
        IServiceProvider services,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(optionsSection);

        IHapticBackend backend = _kind switch
        {
            "bhaptics_vest" => BuildVest(descriptor, optionsSection),
            "bhaptics_sleeve_l" => BuildSleeve(descriptor, optionsSection, side: "left"),
            "bhaptics_sleeve_r" => BuildSleeve(descriptor, optionsSection, side: "right"),
            "bhaptics_feet_l" => BuildFeet(descriptor, optionsSection, side: "left"),
            "bhaptics_feet_r" => BuildFeet(descriptor, optionsSection, side: "right"),
            _ => throw new InvalidOperationException(
                $"BhapticsBackendFactory was constructed with unrecognised kind '{_kind}'"),
        };

        if (!string.IsNullOrEmpty(descriptor.DisplayName))
        {
            // OverrideDisplayName lives on the base class; cast to it so
            // we don't need three overloads.
            ((BhapticsBackendBase)backend).OverrideDisplayName(descriptor.DisplayName);
        }

        return backend;
    }

    private BhapticsVestBackend BuildVest(
        BackendDescriptor descriptor, IConfigurationSection optionsSection)
    {
        var options = optionsSection.Get<BhapticsVestOptions>() ?? new BhapticsVestOptions();
        if (!string.IsNullOrEmpty(descriptor.Id))
        {
            options.BackendId = descriptor.Id;
        }
        return new BhapticsVestBackend(
            options,
            _sdk,
            _time,
            _loggerFactory.CreateLogger<BhapticsVestBackend>());
    }

    private BhapticsSleeveBackend BuildSleeve(
        BackendDescriptor descriptor, IConfigurationSection optionsSection, string side)
    {
        var options = optionsSection.Get<BhapticsSleeveOptions>() ?? new BhapticsSleeveOptions();
        if (!string.IsNullOrEmpty(descriptor.Id))
        {
            options.BackendId = descriptor.Id;
        }
        return new BhapticsSleeveBackend(
            side,
            options,
            _sdk,
            _time,
            _loggerFactory.CreateLogger<BhapticsSleeveBackend>());
    }

    private BhapticsFeetBackend BuildFeet(
        BackendDescriptor descriptor, IConfigurationSection optionsSection, string side)
    {
        var options = optionsSection.Get<BhapticsFeetOptions>() ?? new BhapticsFeetOptions();
        if (!string.IsNullOrEmpty(descriptor.Id))
        {
            options.BackendId = descriptor.Id;
        }
        return new BhapticsFeetBackend(
            side,
            options,
            _sdk,
            _time,
            _loggerFactory.CreateLogger<BhapticsFeetBackend>());
    }

    /// <summary>
    /// The five real bHaptics kinds, in the order
    /// <see cref="AddBhapticsBackendIfWindows"/> registers them. Public
    /// so reflective registration can iterate without rediscovering
    /// the literals.
    /// </summary>
    public static IReadOnlyList<string> SupportedKinds { get; } = new[]
    {
        "bhaptics_vest",
        "bhaptics_sleeve_l",
        "bhaptics_sleeve_r",
        "bhaptics_feet_l",
        "bhaptics_feet_r",
    };
}
#endif

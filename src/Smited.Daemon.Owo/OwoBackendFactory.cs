// This file is excluded from compile on non-Windows hosts via the
// conditional <Compile Remove="OwoBackendFactory.cs"/> ItemGroup in
// Smited.Daemon.Owo.csproj. The body is additionally guarded by
// `#if WINDOWS` for IDE clarity.

#if WINDOWS
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Smited.Daemon.Backends;
using Smited.Daemon.Configuration;

namespace Smited.Daemon.Owo;

/// <summary>
/// Factory for the real <see cref="OwoBackend"/>. Binds the descriptor's
/// <c>Options</c> sub-section to a fresh <see cref="OwoBackendOptions"/>,
/// applies the descriptor's <see cref="BackendDescriptor.Id"/> as a
/// <see cref="OwoBackendOptions.BackendId"/> override, then resolves the
/// rest of the constructor dependencies (<c>IOwoSdk</c>, <c>TimeProvider</c>,
/// <c>ILogger&lt;OwoBackend&gt;</c>) from the daemon DI container via
/// <see cref="ActivatorUtilities.CreateInstance{T}(IServiceProvider, object[])"/>.
/// </summary>
/// <remarks>
/// <para>
/// Public so the daemon host's <c>BackendsServiceCollectionExtensions</c>
/// can reflectively load it via <c>Type.GetType</c> on Windows hosts —
/// the daemon host does not have a compile-time dependency on
/// <c>Smited.Daemon.Owo</c>, so the type and its constructor must be
/// public for cross-assembly instantiation to work.
/// </para>
/// <para>
/// Returns a fresh <see cref="OwoBackend"/> instance per call. Multi-OWO
/// (two descriptors of kind <c>owo_skin</c>) technically works at this
/// layer, but the daemon's <see cref="IOwoSdk"/> registration is a
/// singleton — two backends sharing the same SDK instance will fight
/// over the device. PiShock and bHaptics are the more interesting
/// multi-instance cases; multi-OWO support is deferred until they land.
/// </para>
/// </remarks>
public sealed class OwoBackendFactory : IBackendFactory
{
    /// <inheritdoc />
    public string Kind => "owo_skin";

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

        // Bind the descriptor's Options sub-section to a fresh options
        // instance. Get<T>() returns null when the section has no
        // children (the user omitted Options entirely), in which case
        // we fall through to defaults — same as the legacy boolean
        // path that constructed `new OwoBackendOptions()` when the
        // user didn't supply a value.
        var options = optionsSection.Get<OwoBackendOptions>() ?? new OwoBackendOptions();

        // Descriptor-level Id wins over Options.BackendId so users can
        // change instance identity without rewriting the nested
        // section. Empty descriptor Id falls back to the bound or
        // default value of options.BackendId.
        if (!string.IsNullOrEmpty(descriptor.Id))
        {
            options.BackendId = descriptor.Id;
        }

        return ActivatorUtilities.CreateInstance<OwoBackend>(services, options);
    }
}
#endif

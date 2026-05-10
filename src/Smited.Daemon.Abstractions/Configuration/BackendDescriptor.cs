namespace Smited.Daemon.Configuration;

/// <summary>
/// Configuration entry describing one backend instance the daemon should
/// bring online at startup. Replaces the per-backend boolean wall (the
/// former <c>EnableMockOwo</c>/<c>EnableOwo</c>/<c>Owo</c> shape) that
/// previously lived on <c>SmitedOptions.BackendsOptions</c>.
/// </summary>
/// <remarks>
/// <para>
/// One descriptor produces one backend registered in
/// <c>BackendRegistry</c>. The <see cref="Kind"/> determines which
/// <c>IBackendFactory</c> the bootstrapper invokes; everything else on
/// the descriptor is per-instance overrides and configuration that the
/// factory consumes.
/// </para>
/// <para>
/// Lives in the abstractions assembly so platform-conditional backend
/// projects (e.g. <c>Smited.Daemon.Owo</c>) can implement
/// <c>IBackendFactory</c> against this type without taking a
/// compile-time dependency on the daemon host.
/// </para>
/// <para>
/// Multiple descriptors of the same <see cref="Kind"/> are allowed in
/// principle — for example, two <c>pishock</c> descriptors when the user
/// has two shockers. <c>mock_owo</c> is an exception: the underlying
/// <c>MockOwoBackend</c> is a DI singleton, so the descriptor validator
/// rejects more than one <c>mock_owo</c> entry. The <see cref="Id"/>
/// property must be unique across all descriptors.
/// </para>
/// <para>
/// Per-kind options (e.g. <c>OwoBackendOptions</c>) live under
/// <c>Smited:Backends:Items:{i}:Options</c> in configuration. The
/// bootstrapper resolves the matching
/// <see cref="Microsoft.Extensions.Configuration.IConfigurationSection"/>
/// by index and passes it to the factory so the factory can bind the
/// section to its kind-specific options type. The descriptor itself
/// stays a flat POCO to keep
/// <c>Microsoft.Extensions.Configuration.Binder</c> happy — the binder
/// does not bind <c>IConfigurationSection</c> properties.
/// </para>
/// </remarks>
public sealed class BackendDescriptor
{
    /// <summary>
    /// Backend kind discriminator. Recognized values today:
    /// <c>"mock_owo"</c>, <c>"owo_skin"</c>. Future PRs add
    /// <c>"mock_bhaptics_tactsuit"</c>, <c>"bhaptics_tactsuit"</c>,
    /// <c>"bhaptics_tactglove"</c>, <c>"bhaptics_tactsleeve"</c>,
    /// <c>"bhaptics_tactosy"</c>, <c>"mock_pishock"</c>,
    /// <c>"pishock"</c>. Matched case-insensitively against
    /// <c>IBackendFactory.Kind</c> when resolving a factory.
    /// </summary>
    public string Kind { get; set; } = "";

    /// <summary>
    /// Unique runtime identifier for this backend instance. Surfaces
    /// through <c>IHapticBackend.Id</c> and is what clients address via
    /// <c>backend_id</c> in gRPC calls. Must be unique across every
    /// descriptor in the active configuration and match the IDENT regex
    /// <c>^[a-z0-9][a-z0-9_-]*$</c>.
    /// </summary>
    public string Id { get; set; } = "";

    /// <summary>
    /// When <c>false</c>, the descriptor is parsed and validated but
    /// not brought online. Useful for keeping configuration for hardware
    /// the user temporarily disconnects without deleting the entry.
    /// Default <c>true</c>.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Optional human-readable display name override. When unset, the
    /// factory uses its kind-default (e.g. "OWO Skin", "Mock OWO Skin").
    /// </summary>
    public string? DisplayName { get; set; }
}

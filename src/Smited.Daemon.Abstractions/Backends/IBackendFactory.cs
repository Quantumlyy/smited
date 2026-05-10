using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Smited.Daemon.Configuration;

namespace Smited.Daemon.Backends;

/// <summary>
/// Factory for one backend kind. Resolves a <see cref="BackendDescriptor"/>
/// from configuration into a fully-configured <see cref="IHapticBackend"/>
/// instance ready for <c>BackendBootstrapper</c> to <c>ConnectAsync</c>
/// and register.
/// </summary>
/// <remarks>
/// <para>
/// Each kind ships exactly one factory. Factories are registered in DI
/// via the standard <c>services.AddSingleton&lt;IBackendFactory,
/// FooFactory&gt;()</c> pattern; the bootstrapper resolves
/// <c>IEnumerable&lt;IBackendFactory&gt;</c> and dispatches by
/// <see cref="Kind"/> using a case-insensitive match.
/// </para>
/// <para>
/// A factory may decline to create an instance (return <c>null</c>) when
/// the runtime environment doesn't support it — for example, the OWO
/// factory returns null when its SDK runtime dependencies failed to
/// load. The bootstrapper logs the decline at info level and continues
/// with other descriptors.
/// </para>
/// <para>
/// Lives in the abstractions assembly so platform-conditional backend
/// projects (e.g. <c>Smited.Daemon.Owo</c>) can implement this
/// interface without a compile-time dependency on the daemon host.
/// Public for the same reason — the OWO assembly is a separate
/// compilation unit.
/// </para>
/// <para>
/// <strong>Singleton-state factories:</strong> if the factory's
/// backend shares state across instances — a DI singleton backend
/// object, a static SDK, or a single hardware connection that two
/// backends would race on — add the kind to
/// <c>BackendDescriptorValidator.SingletonKinds</c> so the validator
/// rejects user configurations that try to register two of them. The
/// alternative (silent state corruption when two descriptors of the
/// same kind both register) is far worse than the up-front error.
/// </para>
/// </remarks>
public interface IBackendFactory
{
    /// <summary>
    /// Discriminator the factory matches against
    /// <see cref="BackendDescriptor.Kind"/>. Case-insensitive.
    /// </summary>
    string Kind { get; }

    /// <summary>
    /// Build an <see cref="IHapticBackend"/> from the descriptor, or
    /// return <c>null</c> if this environment can't host it (wrong OS,
    /// missing assembly, broken runtime dep). Throws only on
    /// genuinely-unrecoverable misconfiguration the user must fix;
    /// otherwise log-and-decline via null return.
    /// </summary>
    /// <param name="descriptor">User-supplied descriptor entry.</param>
    /// <param name="optionsSection">
    /// Raw configuration section for the descriptor's <c>Options</c>
    /// sub-tree. Empty (no children) when the user omitted
    /// <c>Options</c>; the factory falls back to its kind defaults in
    /// that case.
    /// </param>
    /// <param name="services">Daemon-wide DI container.</param>
    /// <param name="logger">Bootstrapper logger for decline notes.</param>
    IHapticBackend? TryCreate(
        BackendDescriptor descriptor,
        IConfigurationSection optionsSection,
        IServiceProvider services,
        ILogger logger);
}

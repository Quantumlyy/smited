using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Smited.Daemon.Backends.Mock;
using Smited.Daemon.Pishock;

namespace Smited.Daemon.Backends;

/// <summary>
/// Composition-root helpers that register the built-in backend factories
/// and any reflectively-loaded platform-conditional factories.
/// </summary>
internal static class BackendsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the cross-platform backend factories. Adds the
    /// <see cref="MockOwoBackendFactory"/> in DI alongside the existing
    /// <see cref="MockOwoBackend"/> singleton.
    /// </summary>
    public static IServiceCollection AddSmitedBackends(this IServiceCollection services)
    {
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IBackendFactory, MockOwoBackendFactory>());
        // PiShock is cross-platform and multi-instance — no platform
        // conditional, no AddPishockBackendIfX gate. Both the mock and
        // real factories register unconditionally; descriptors that
        // don't apply leave them idle in DI.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IBackendFactory, MockPishockBackendFactory>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IBackendFactory, PishockBackendFactory>());
        return services;
    }

    /// <summary>
    /// Reflectively loads the OWO backend's factory and SDK on Windows
    /// hosts. No-op on non-Windows. Replaces the previous inline
    /// reflective registration in <c>Program.cs</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Smited.Daemon.Owo</c> is referenced from the daemon's csproj
    /// with <c>ReferenceOutputAssembly=false</c> so the daemon doesn't
    /// gain a compile-time dependency on Windows-only OWO types.
    /// That setting also keeps the assembly out of the daemon's
    /// <c>.deps.json</c> runtime list, so
    /// <see cref="Type.GetType(string)"/> with an
    /// <c>"type, assembly"</c> spec returns null even with the DLL
    /// sitting next to the executable: the runtime resolver only
    /// probes the TPA list and already-loaded assemblies. Explicit
    /// <see cref="Assembly.LoadFrom(string)"/> reads the sibling DLL
    /// directly, after which <see cref="Assembly.GetType(string)"/>
    /// resolves type names from the loaded module's metadata.
    /// </para>
    /// <para>
    /// The reflective lookup is wrapped in a deliberately broad
    /// <c>catch (Exception)</c>: the assembly load and type resolution
    /// surface an open-ended set of failure modes
    /// (<see cref="FileNotFoundException"/> when the DLL hasn't been
    /// copied — the expected case on Windows hosts without OWO,
    /// <see cref="BadImageFormatException"/> for a wrong-architecture
    /// DLL, <see cref="FileLoadException"/> for missing transitive
    /// deps, <see cref="TypeLoadException"/> /
    /// <see cref="System.Reflection.ReflectionTypeLoadException"/> for
    /// type-resolution failures, <see cref="PlatformNotSupportedException"/>
    /// on a downlevel runtime, etc.). All of them mean the same thing
    /// — "OWO unavailable here" — and the right response is the
    /// same: log, register no factory, daemon continues. We surface
    /// the error to <c>Console.Error</c> because the host logger
    /// isn't online when this runs.
    /// </para>
    /// <para>
    /// Both the <c>OwoBackendFactory</c> and <c>StaticOwoSdk</c>
    /// reflective registrations live here so the bootstrapper can
    /// stay generic and not know about platform-conditional types.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddOwoBackendIfWindows(this IServiceCollection services)
    {
        if (!OperatingSystem.IsWindows())
        {
            return services;
        }

        Type? factoryType;
        Type? sdkType;
        try
        {
            var owoAssemblyPath = Path.Combine(AppContext.BaseDirectory, "Smited.Daemon.Owo.dll");
            var owoAssembly = Assembly.LoadFrom(owoAssemblyPath);

            factoryType = owoAssembly.GetType("Smited.Daemon.Owo.OwoBackendFactory");
            sdkType = owoAssembly.GetType("Smited.Daemon.Owo.StaticOwoSdk");
        }
        catch (Exception ex)
        {
            // Broad catch is intentional. Any failure to resolve an
            // OWO type means the assembly is unusable in this
            // environment — missing file (FileNotFoundException),
            // wrong architecture (BadImageFormatException), missing
            // transitive dependency (FileLoadException), corrupt PE,
            // type-resolution failure (TypeLoadException /
            // ReflectionTypeLoadException), PlatformNotSupportedException
            // on a downlevel runtime, and so on. The recoverable set
            // is open-ended and the right response is uniform: log
            // and continue without OWO support. A narrower filter
            // regressed BadImageFormatException coverage in a
            // previous round and crashed daemon startup on
            // misconfigured Windows installs. Don't re-tighten
            // without checking which exception types this is
            // load-bearing for.
            Console.Error.WriteLine(
                $"warn: OWO assembly load failed ({ex.GetType().Name}). "
                + "Daemon will continue without OWO support; verify the "
                + "Smited.Daemon.Owo assembly and OWO.dll runtime files "
                + "are present and built for the current architecture. "
                + $"Underlying error: {ex.Message}");
            return services;
        }

        // Atomic registration: factory and SDK go in together, or
        // neither does. A factory registered without its IOwoSdk
        // dependency would throw at TryCreate time when the
        // descriptor reaches it; round-N+6's narrow exception
        // classifier in BackendBootstrapper correctly treats that
        // throw as user-fixable misconfiguration and aborts startup.
        // The right place to handle the partial-load case is here,
        // up-front, with a clear warning naming the actual
        // filesystem state — not at first-trigger time.
        if (factoryType is null || sdkType is null)
        {
            // Asymmetric diagnostics: each partial-load shape gets a
            // message naming what's missing so the user knows where
            // to look. Both-null after a successful Assembly.LoadFrom
            // means the DLL exists but contains neither type — a
            // malformed assembly that warrants a clear warning rather
            // than silence. (The no-OWO-installed case takes the
            // FileNotFoundException branch in the catch above; non-
            // Windows hosts short-circuit before reaching this code
            // at all.)
            if (factoryType is not null && sdkType is null)
            {
                Console.Error.WriteLine(
                    "warn: OWO factory type loaded but StaticOwoSdk did not. "
                    + "This indicates a partial OWO assembly install. "
                    + "Skipping OWO registration; daemon continues without "
                    + "OWO support. Rebuild/republish to refresh the OWO "
                    + "runtime files.");
            }
            else if (factoryType is null && sdkType is not null)
            {
                Console.Error.WriteLine(
                    "warn: OWO StaticOwoSdk type loaded but OwoBackendFactory "
                    + "did not. This indicates a partial OWO assembly "
                    + "install. Skipping OWO registration.");
            }
            else
            {
                Console.Error.WriteLine(
                    "warn: OWO assembly loaded but neither OwoBackendFactory "
                    + "nor StaticOwoSdk could be resolved from it. The DLL "
                    + "is malformed or stripped. Skipping OWO registration; "
                    + "daemon continues without OWO support.");
            }
            return services;
        }

        services.AddSingleton(typeof(IOwoSdk), sdkType);
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton(typeof(IBackendFactory), factoryType));
        return services;
    }
}

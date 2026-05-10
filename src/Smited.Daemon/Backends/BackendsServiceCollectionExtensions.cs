using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Smited.Daemon.Backends.Mock;

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
        return services;
    }

    /// <summary>
    /// Reflectively loads the OWO backend's factory and SDK on Windows
    /// hosts. No-op on non-Windows. Replaces the previous inline
    /// reflective registration in <c>Program.cs</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reflective lookup is wrapped in a deliberately broad
    /// <c>catch (Exception)</c>: <see cref="Type.GetType(string)"/>
    /// surfaces an open-ended set of failure modes when the assembly
    /// is present but unusable in this environment
    /// (<see cref="BadImageFormatException"/> for a wrong-architecture
    /// DLL, <see cref="FileNotFoundException"/> /
    /// <see cref="FileLoadException"/> for missing transitive deps,
    /// <see cref="TypeLoadException"/> /
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

        var factoryType = TryLoadType(
            "Smited.Daemon.Owo.OwoBackendFactory, Smited.Daemon.Owo",
            "OwoBackendFactory");
        if (factoryType is not null)
        {
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton(typeof(IBackendFactory), factoryType));
        }

        var sdkType = TryLoadType(
            "Smited.Daemon.Owo.StaticOwoSdk, Smited.Daemon.Owo",
            "StaticOwoSdk");
        if (sdkType is not null)
        {
            services.AddSingleton(typeof(IOwoSdk), sdkType);
        }

        return services;
    }

    private static Type? TryLoadType(string assemblyQualifiedName, string shortName)
    {
        try
        {
            var type = Type.GetType(assemblyQualifiedName);
            if (type is null)
            {
                Console.Error.WriteLine(
                    $"warn: Skipping {shortName} registration; the "
                    + "Smited.Daemon.Owo assembly is not in the output "
                    + "directory. Rebuild/republish to land it.");
            }
            return type;
        }
        catch (Exception ex)
        {
            // Broad catch is intentional. Any failure to resolve the OWO
            // type means the assembly is unusable in this environment —
            // missing file, wrong architecture (BadImageFormatException),
            // missing transitive dependency (FileNotFoundException /
            // FileLoadException), corrupt PE, type-resolution failure
            // (TypeLoadException / ReflectionTypeLoadException),
            // PlatformNotSupportedException on a downlevel runtime, and so
            // on. The recoverable set is open-ended and the right response
            // is uniform: register no factory and let the daemon continue
            // without OWO support. A narrower filter regressed
            // BadImageFormatException coverage in a previous round and
            // crashed daemon startup on misconfigured Windows installs.
            // Don't re-tighten without checking which exception types this
            // is load-bearing for.
            Console.Error.WriteLine(
                $"warn: Skipping {shortName} registration; reflective load "
                + $"threw {ex.GetType().Name}. Daemon will continue without "
                + "OWO support; verify the Smited.Daemon.Owo assembly and "
                + "OWO.dll runtime files are present and built for the "
                + $"current architecture. Underlying error: {ex.Message}");
            return null;
        }
    }
}

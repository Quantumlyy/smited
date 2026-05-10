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
    /// The reflective lookup is wrapped in the same
    /// <see cref="FileNotFoundException"/>/<see cref="FileLoadException"/>/<see cref="TypeLoadException"/>
    /// catch as the previous bootstrapper code: <see cref="Type.GetType(string)"/>
    /// can throw even with the default <c>throwOnError=false</c> when
    /// the assembly is present but a transitive runtime dependency
    /// (e.g. OWO.dll) is missing or unloadable. We surface the error
    /// to <c>Console.Error</c> because the host logger isn't online
    /// when this runs, and continue without registering — the daemon
    /// stays up; OWO triggers will be rejected as if the assembly
    /// were absent.
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
            when (ex is FileNotFoundException or FileLoadException or TypeLoadException)
        {
            Console.Error.WriteLine(
                $"warn: Skipping {shortName} registration; reflective load "
                + $"threw {ex.GetType().Name}. Likely cause: the Smited.Daemon.Owo "
                + "assembly is present but its OWO.dll runtime dependency "
                + $"isn't next to it. Underlying error: {ex.Message}");
            return null;
        }
    }
}

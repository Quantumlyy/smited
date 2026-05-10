using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Smited.Daemon.Backends;
using Smited.Daemon.Configuration;
using Smited.Daemon.Pishock.Internal;

namespace Smited.Daemon.Pishock;

/// <summary>
/// Factory for the real <see cref="PishockBackend"/>. Each descriptor
/// produces a fresh backend instance with its own
/// <see cref="IPishockClient"/>; cloud-mode descriptors get a
/// <see cref="CloudPishockClient"/> and LAN-mode descriptors get a
/// <see cref="LanPishockClient"/>.
/// </summary>
/// <remarks>
/// <para>
/// Validation that surfaces as <see cref="BackendConfigurationException"/>:
/// the shared rules from <see cref="MockPishockBackendFactory.ValidateOptions"/>
/// (AllowedOps non-empty, intensity caps in 0..100, positive durations
/// and rate limits), plus transport-specific rules:
/// </para>
/// <list type="bullet">
///   <item>Cloud mode requires <c>Username</c>, <c>ApiKey</c>, and <c>ShareCode</c>.</item>
///   <item>LAN mode requires <c>DeviceIp</c>.</item>
/// </list>
/// </remarks>
public sealed class PishockBackendFactory : IBackendFactory
{
    public string Kind => "pishock";

    public IHapticBackend? TryCreate(
        BackendDescriptor descriptor,
        IConfigurationSection optionsSection,
        IServiceProvider services,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(optionsSection);
        ArgumentNullException.ThrowIfNull(services);

        var options = optionsSection.Get<PishockBackendOptions>() ?? new PishockBackendOptions();

        // Same precedence as MockPishockBackendFactory: descriptor.DisplayName
        // is the documented override surface, takes precedence over
        // Options.DisplayName.
        if (!string.IsNullOrEmpty(descriptor.DisplayName))
        {
            options.DisplayName = descriptor.DisplayName;
        }

        // Re-use the mock factory's shared validation rules (AllowedOps,
        // intensity caps, durations, rate limits) so the real and mock
        // surfaces enforce the same constraints with a single source of
        // truth.
        MockPishockBackendFactory.ValidateOptions(descriptor, options);

        ValidateTransportOptions(descriptor, options);

        var client = options.Mode switch
        {
            PishockTransportMode.Cloud => BuildCloudClient(descriptor, options, services),
            PishockTransportMode.Lan => BuildLanClient(descriptor, options, services),
            _ => throw new BackendConfigurationException(descriptor.Id, descriptor.Kind,
                $"unknown transport Mode='{options.Mode}'; valid values are Cloud or Lan"),
        };

        return ActivatorUtilities.CreateInstance<PishockBackend>(
            services, descriptor.Id, options, client);
    }

    private static void ValidateTransportOptions(
        BackendDescriptor descriptor, PishockBackendOptions options)
    {
        switch (options.Mode)
        {
            case PishockTransportMode.Cloud:
                var missing = new List<string>();
                if (string.IsNullOrWhiteSpace(options.Username)) missing.Add(nameof(options.Username));
                if (string.IsNullOrWhiteSpace(options.ApiKey)) missing.Add(nameof(options.ApiKey));
                if (string.IsNullOrWhiteSpace(options.ShareCode)) missing.Add(nameof(options.ShareCode));
                if (missing.Count > 0)
                {
                    throw new BackendConfigurationException(descriptor.Id, descriptor.Kind,
                        $"Cloud mode requires {string.Join(", ", missing)}; missing fields make "
                        + "the cloud API reject every request as malformed. Get a username + API key + "
                        + "share code from the PiShock account UI and paste them into Options.");
                }
                break;

            case PishockTransportMode.Lan:
                if (string.IsNullOrWhiteSpace(options.DeviceIp))
                {
                    throw new BackendConfigurationException(descriptor.Id, descriptor.Kind,
                        "LAN mode requires DeviceIp. Find the device's IP via your router's DHCP "
                        + "lease list or the PiShock mobile app's device-info screen.");
                }
                break;
        }
    }

    private static IPishockClient BuildCloudClient(
        BackendDescriptor descriptor, PishockBackendOptions options, IServiceProvider services)
    {
        // Each descriptor gets its own HttpClient. Sharing across
        // descriptors via IHttpClientFactory would be a small efficiency
        // win for cloud-heavy setups but adds a registration the user
        // doesn't see in their config; the per-descriptor cost is
        // negligible at the expected scale (a handful of shockers).
        var http = new HttpClient();
        var logger = services.GetRequiredService<ILogger<CloudPishockClient>>();
        return new CloudPishockClient(http, options, descriptor.Id, logger);
    }

    private static IPishockClient BuildLanClient(
        BackendDescriptor descriptor, PishockBackendOptions options, IServiceProvider services)
    {
        var http = new HttpClient();
        var logger = services.GetRequiredService<ILogger<LanPishockClient>>();
        return new LanPishockClient(http, options, descriptor.Id, logger);
    }
}

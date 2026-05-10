using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Smited.Daemon.Backends.Internal;
using Smited.Daemon.Configuration;
using Smited.Daemon.Events;

namespace Smited.Daemon.Backends;

/// <summary>
/// Implements <see cref="IHostedService"/> directly (not
/// <see cref="BackgroundService"/>) so backend registration runs
/// synchronously in <c>StartAsync</c>. Registered before
/// <see cref="Sensations.SensationLoader"/> in DI so the loader sees
/// the populated registry.
/// </summary>
/// <remarks>
/// <para>
/// Iterates <see cref="SmitedOptions.BackendsOptions.Items"/> and, for
/// each entry, resolves a matching <see cref="IBackendFactory"/>
/// (case-insensitive on <see cref="BackendDescriptor.Kind"/>) and asks
/// it to build the backend from the descriptor and its
/// <c>Smited:Backends:Items:{i}:Options</c> sub-section. Factories
/// that decline (return <c>null</c>) — typically because the host OS
/// or runtime can't host the backend — are logged and skipped without
/// aborting startup.
/// </para>
/// <para>
/// For each registered backend, also spins up a fan-out task that
/// forwards backend lifecycle events to <see cref="EventBus"/>.
/// </para>
/// </remarks>
internal sealed class BackendBootstrapper : IHostedService
{
    private readonly BackendRegistry _registry;
    private readonly EventBus _bus;
    private readonly SmitedOptions _options;
    private readonly IConfiguration _configuration;
    private readonly IServiceProvider _services;
    private readonly IReadOnlyList<IBackendFactory> _factories;
    private readonly IEnumerable<IHapticBackend> _additionalBackends;
    private readonly ILogger<BackendBootstrapper> _logger;
    private readonly List<Task> _fanTasks = new();
    private readonly List<IHapticBackend> _registered = new();
    private readonly CancellationTokenSource _stopping = new();

    public BackendBootstrapper(
        BackendRegistry registry,
        EventBus bus,
        IOptions<SmitedOptions> options,
        IConfiguration configuration,
        IServiceProvider services,
        IEnumerable<IBackendFactory> factories,
        IEnumerable<IHapticBackend> additionalBackends,
        ILogger<BackendBootstrapper> logger)
    {
        _registry = registry;
        _bus = bus;
        _options = options.Value;
        _configuration = configuration;
        _services = services;
        _factories = factories.ToArray();
        _additionalBackends = additionalBackends;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var descriptors = _options.Backends.Items;

        var validationErrors = BackendDescriptorValidator.Validate(descriptors);
        if (validationErrors.Count > 0)
        {
            var combined = string.Join(Environment.NewLine, validationErrors);
            throw new OptionsValidationException(
                nameof(SmitedOptions.BackendsOptions),
                typeof(SmitedOptions.BackendsOptions),
                new[]
                {
                    "Backend descriptor configuration is invalid:" + Environment.NewLine + combined,
                });
        }

        for (var i = 0; i < descriptors.Count; i++)
        {
            var descriptor = descriptors[i];
            if (!descriptor.Enabled)
            {
                _logger.LogInformation(
                    "Skipping disabled descriptor {Id} ({Kind})",
                    descriptor.Id, descriptor.Kind);
                continue;
            }

            var factory = ResolveFactory(descriptor.Kind);
            if (factory is null)
            {
                _logger.LogWarning(
                    "No factory registered for backend kind {Kind} (descriptor id {Id}); skipping",
                    descriptor.Kind, descriptor.Id);
                continue;
            }

            var optionsSection = _configuration.GetSection(
                $"Smited:Backends:Items:{i}:Options");

            IHapticBackend? backend;
            try
            {
                backend = factory.TryCreate(descriptor, optionsSection, _services, _logger);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Factory for kind {Kind} threw while creating descriptor {Id}",
                    descriptor.Kind, descriptor.Id);
                continue;
            }

            if (backend is null)
            {
                _logger.LogInformation(
                    "Factory for kind {Kind} declined to create descriptor {Id} "
                    + "(likely environmental: wrong OS, missing assembly)",
                    descriptor.Kind, descriptor.Id);
                continue;
            }

            if (await RegisterAndFan(backend, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogInformation(
                    "Registered backend {Id} ({Kind}: {DisplayName})",
                    backend.Id, backend.Kind, backend.DisplayName);
            }
        }

        // _additionalBackends preserves the existing test injection
        // seam: anything registered as IHapticBackend in the DI
        // container shows up here without needing a descriptor entry.
        foreach (var backend in _additionalBackends)
        {
            if (await RegisterAndFan(backend, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogInformation("Registered backend {Id} ({Kind}: {DisplayName})",
                    backend.Id, backend.Kind, backend.DisplayName);
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _stopping.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException) { }

        try
        {
            await Task.WhenAll(_fanTasks).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Don't block shutdown if a fan task is misbehaving.
        }
        catch (TimeoutException)
        {
            // Same — drop the fan task so it gets GC'd later.
        }

        foreach (var backend in _registered)
        {
            try
            {
                await backend.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Backend {BackendId} threw on disposal", backend.Id);
            }
        }
    }

    private IBackendFactory? ResolveFactory(string kind) =>
        _factories.FirstOrDefault(f =>
            string.Equals(f.Kind, kind, StringComparison.OrdinalIgnoreCase));

    private async Task<bool> RegisterAndFan(IHapticBackend backend, CancellationToken cancellationToken)
    {
        try
        {
            await backend.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Skipping backend {BackendId}: ConnectAsync failed",
                BackendIdForLog(backend));
            return false;
        }

        try
        {
            _registry.Register(backend);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Skipping backend {BackendId}: registration failed",
                BackendIdForLog(backend));
            return false;
        }

        _registered.Add(backend);
        _fanTasks.Add(Task.Run(() => FanEventsAsync(backend, _stopping.Token)));
        return true;
    }

    private static string BackendIdForLog(IHapticBackend backend)
    {
        try
        {
            return backend.Id;
        }
        catch
        {
            return backend.GetType().FullName ?? backend.GetType().Name;
        }
    }

    private async Task FanEventsAsync(IHapticBackend backend, CancellationToken ct)
    {
        try
        {
            await foreach (var evt in backend.Events.WithCancellation(ct).ConfigureAwait(false))
            {
                _bus.Publish(evt);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fan-out task for backend {BackendId} crashed", backend.Id);
        }
    }
}

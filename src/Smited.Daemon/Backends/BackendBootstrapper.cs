using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Smited.Daemon.Backends.Internal;
using Smited.Daemon.BodyMap;
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
    private readonly BodyMapValidator _bodyMapValidator;
    private readonly BodyMapState _bodyMapState;
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
        BodyMapValidator bodyMapValidator,
        BodyMapState bodyMapState,
        ILogger<BackendBootstrapper> logger)
    {
        _registry = registry;
        _bus = bus;
        _options = options.Value;
        _configuration = configuration;
        _services = services;
        _factories = factories.ToArray();
        _additionalBackends = additionalBackends;
        _bodyMapValidator = bodyMapValidator;
        _bodyMapState = bodyMapState;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<BackendDescriptor> descriptors = _options.Backends.Items;

        // Validate user-supplied descriptors first so configuration
        // mistakes surface even when the fallback kicks in for an
        // accidentally-empty list. The synthesized default below is
        // well-formed by construction and doesn't need re-validation.
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

        if (descriptors.Count == 0)
        {
            _logger.LogInformation(
                "No backend descriptors configured; registering default mock-owo. "
                + "Configure Smited:Backends:Items with a non-empty array to opt out.");
            descriptors = new[]
            {
                new BackendDescriptor
                {
                    Kind = "mock_owo",
                    Id = "mock-owo",
                    Enabled = true,
                },
            };
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

        await ValidateBodyMapAsync().ConfigureAwait(false);
    }

    private async Task ValidateBodyMapAsync()
    {
        var bodyMap = _options.BodyMap;
        var initial = _bodyMapValidator.Validate(_registry.All, bodyMap);

        // UnknownBackend / UnknownZone are user typos; abort startup so
        // the user fixes them at once rather than silently coexisting.
        var fatal = initial.Errors
            .Where(e => e.Kind is BodyMapErrorKind.UnknownBackend or BodyMapErrorKind.UnknownZone)
            .ToList();
        if (fatal.Count > 0)
        {
            throw new OptionsValidationException(
                nameof(SmitedOptions.BodyMap),
                typeof(BodyMapOptions),
                fatal.Select(e => e.Message));
        }

        // Forbidden-region errors get the offending backend deregistered.
        // Manufacturer errors are non-overridable; smited-default errors
        // could be overridden by AllowOverrideRegions but we already
        // accounted for that inside the validator (those don't surface
        // when the override is set), so reaching here means refusal.
        var refusedIds = initial.Errors
            .Where(e => e.Kind is BodyMapErrorKind.ManufacturerForbidden
                or BodyMapErrorKind.SmitedDefaultForbidden)
            .Select(e => e.BackendId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var error in initial.Errors)
        {
            _logger.LogError(
                "Bodymap rejection [{Kind}]: {Message}",
                error.Kind, error.Message);
        }

        foreach (var refusedId in refusedIds)
        {
            if (_registry.Deregister(refusedId))
            {
                var refused = _registered.Find(b => string.Equals(b.Id, refusedId, StringComparison.OrdinalIgnoreCase));
                if (refused is not null)
                {
                    _registered.Remove(refused);
                    try
                    {
                        await refused.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "Backend {BackendId} threw while disposing after bodymap refusal", refusedId);
                    }
                }
            }
        }

        // Re-run validation against the survivors so the persisted state
        // reflects the post-refusal world. The first pass's warnings
        // could still mention refused backends; re-running yields the
        // accurate overlap picture for the trigger-time check.
        var finalResult = refusedIds.Count > 0
            ? _bodyMapValidator.Validate(_registry.All, bodyMap)
            : initial;

        foreach (var warning in finalResult.Warnings)
        {
            _logger.LogWarning("Bodymap overlap: {Message}", warning.Message);
        }

        _bodyMapState.Initialize(
            finalResult,
            bodyMap.OverlapPolicy,
            placementCount: bodyMap.Placements.Count,
            refusedBackendCount: refusedIds.Count);
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

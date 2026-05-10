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
                // The IBackendFactory contract: throws are
                // user-fixable misconfiguration; environmental decline
                // is communicated by returning null. Conflating the
                // two would mean a typo like
                // `Options:HeartbeatSeconds = "abc"` silently skips
                // the backend at startup — the user gets a daemon
                // that's missing the backend they configured, with
                // only a log line to find. Surface factory exceptions
                // as startup failures so the user has to fix the
                // config.
                _logger.LogError(ex,
                    "Factory for kind {Kind} threw while creating descriptor {Id}",
                    descriptor.Kind, descriptor.Id);
                throw new InvalidOperationException(
                    $"Backend factory for kind '{descriptor.Kind}' threw while "
                    + $"creating descriptor '{descriptor.Id}'. The "
                    + "IBackendFactory contract treats exceptions as "
                    + "user-fixable misconfiguration (typically a malformed "
                    + "value in the descriptor's Options section). See the "
                    + "inner exception for the underlying cause.",
                    ex);
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

        ValidateBodyMap(descriptors);
    }

    private void ValidateBodyMap(IReadOnlyList<BackendDescriptor> descriptors)
    {
        var bodyMap = _options.BodyMap;

        // declaredIds includes the synthesized default (when Items was
        // empty) so a placement targeting "mock-owo" against the
        // synthesized default reaches the registered branch and a
        // "did you mean" suggestion list isn't empty in the
        // empty-Items case. Disabled descriptors are excluded — a
        // placement targeting a disabled backend is stale config and
        // surfaces as UnknownBackend.
        var declaredIds = descriptors
            .Where(d => d.Enabled)
            .Select(d => d.Id)
            .ToArray();

        var result = _bodyMapValidator.Validate(_registry.All, declaredIds, bodyMap);

        // Every error kind except BackendDeclined is fatal. BackendDeclined
        // is environmental (e.g. an OWO descriptor on a Mac host whose
        // factory returned null); the placement is skipped and startup
        // continues. The fatal/warning split is expressed declaratively
        // here rather than nesting conditionals inside the loop.
        var fatalErrors = result.Errors
            .Where(e => e.Kind != BodyMapErrorKind.BackendDeclined)
            .ToArray();
        var declined = result.Errors
            .Where(e => e.Kind == BodyMapErrorKind.BackendDeclined)
            .ToArray();

        foreach (var error in fatalErrors)
        {
            _logger.LogError("Body map error [{Kind}]: {Message}",
                error.Kind, error.Message);
        }
        foreach (var warn in declined)
        {
            _logger.LogWarning("{Message}", warn.Message);
        }

        if (fatalErrors.Length > 0)
        {
            throw new InvalidOperationException(
                $"Body map has {fatalErrors.Length} fatal "
                + $"error{(fatalErrors.Length == 1 ? "" : "s")}; refusing to start. "
                + "See preceding error logs.");
        }

        foreach (var warning in result.Warnings)
        {
            _logger.LogWarning("Bodymap overlap: {Message}", warning.Message);
        }

        // PlacementCount drives the banner's "N placements" line and
        // its "Not configured (warnings off)" zero-state. The user-
        // facing "placement" is the validator-considered placement,
        // not the raw config-shape placement: a config containing
        // only BodyRegion.Unspecified placeholders is documented as
        // inert (every check is a no-op), and reporting "1 placements"
        // for that case would contradict the inert behavior. Match
        // the banner number to what the body map actually enforces.
        var consideredCount = bodyMap.Placements.Count(
            p => p.Region != BodyRegion.Unspecified);

        _bodyMapState.Initialize(
            result,
            bodyMap.OverlapPolicy,
            placementCount: consideredCount);
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

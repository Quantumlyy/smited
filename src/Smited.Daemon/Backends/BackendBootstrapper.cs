using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Smited.Daemon.Backends.Internal;
using Smited.Daemon.Backends.Mock;
using Smited.Daemon.Configuration;
using Smited.Daemon.Events;

namespace Smited.Daemon.Backends;

/// <summary>
/// Implements <see cref="IHostedService"/> directly (not
/// <see cref="BackgroundService"/>) so backend registration runs
/// synchronously in <c>StartAsync</c>. Registered before
/// <see cref="Sensations.SensationLoader"/> in DI so the loader sees
/// the populated registry.
///
/// For each registered backend, also spins up a fan-out task that
/// forwards backend lifecycle events to <see cref="EventBus"/>.
/// </summary>
internal sealed class BackendBootstrapper : IHostedService
{
    private readonly BackendRegistry _registry;
    private readonly EventBus _bus;
    private readonly SmitedOptions _options;
    private readonly IServiceProvider _services;
    private readonly IEnumerable<IHapticBackend> _additionalBackends;
    private readonly ILogger<BackendBootstrapper> _logger;
    private readonly List<Task> _fanTasks = new();
    private readonly List<IHapticBackend> _registered = new();
    private readonly CancellationTokenSource _stopping = new();

    public BackendBootstrapper(
        BackendRegistry registry,
        EventBus bus,
        IOptions<SmitedOptions> options,
        IServiceProvider services,
        IEnumerable<IHapticBackend> additionalBackends,
        ILogger<BackendBootstrapper> logger)
    {
        _registry = registry;
        _bus = bus;
        _options = options.Value;
        _services = services;
        _additionalBackends = additionalBackends;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_options.Backends.EnableMockOwo)
        {
            var mock = _services.GetRequiredService<MockOwoBackend>();
            if (await RegisterAndFan(mock, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogInformation("Registered backend {Id} ({Kind}: {DisplayName})",
                    mock.Id, mock.Kind, mock.DisplayName);
            }
        }

        if (_options.Backends.EnableMockBhaptics)
        {
            var mock = _services.GetRequiredService<MockBhapticsBackend>();
            if (await RegisterAndFan(mock, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogInformation("Registered backend {Id} ({Kind}: {DisplayName})",
                    mock.Id, mock.Kind, mock.DisplayName);
            }
        }

        if (_options.Backends.EnableOwo)
        {
            if (!OperatingSystem.IsWindows())
            {
                _logger.LogWarning("OWO backend disabled: EnableOwo=true but not running on Windows.");
            }
            else
            {
                // Type.GetType can throw FileNotFoundException /
                // FileLoadException / TypeLoadException even with the
                // default throwOnError=false when the assembly is
                // present but a transitive dependency (OWO.dll) is
                // missing or unloadable. Treat that the same as the
                // assembly-not-found case so daemon startup doesn't
                // crash; the user-facing remediation is the same
                // either way (rebuild/republish to land the OWO
                // runtime files).
                Type? owoType;
                try
                {
                    owoType = Type.GetType("Smited.Daemon.Owo.OwoBackend, Smited.Daemon.Owo");
                    if (owoType is null)
                    {
                        _logger.LogWarning(
                            "OWO backend disabled: EnableOwo=true but the Smited.Daemon.Owo assembly is not in the output directory.");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "OWO backend disabled: reflective load of OwoBackend threw "
                        + "({ExceptionType}). Likely cause: the Smited.Daemon.Owo "
                        + "assembly is present but its OWO.dll runtime dependency "
                        + "isn't next to it. Rebuild/republish to refresh the OWO "
                        + "runtime files.",
                        ex.GetType().Name);
                    owoType = null;
                }

                if (owoType is not null)
                {
                    var owo = (IHapticBackend)ActivatorUtilities.CreateInstance(_services, owoType);
                    if (await RegisterAndFan(owo, cancellationToken).ConfigureAwait(false))
                    {
                        _logger.LogInformation("Registered Windows OWO backend {Id}", owo.Id);
                    }
                }
            }
        }

        if (_options.Backends.EnableBhaptics)
        {
            if (!OperatingSystem.IsWindows())
            {
                _logger.LogWarning(
                    "bHaptics backend disabled: EnableBhaptics=true but not running on Windows. " +
                    "bHaptics Player only runs on Windows.");
            }
            else
            {
                var bhapticsType = Type.GetType("Smited.Daemon.Bhaptics.BhapticsBackend, Smited.Daemon.Bhaptics");
                if (bhapticsType is null)
                {
                    _logger.LogWarning(
                        "bHaptics backend disabled: EnableBhaptics=true but the Smited.Daemon.Bhaptics assembly is not in the output directory.");
                }
                else
                {
                    var bhaptics = (IHapticBackend)ActivatorUtilities.CreateInstance(_services, bhapticsType);
                    if (await RegisterAndFan(bhaptics, cancellationToken).ConfigureAwait(false))
                    {
                        _logger.LogInformation("Registered Windows bHaptics backend {Id}", bhaptics.Id);
                    }
                }
            }
        }

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

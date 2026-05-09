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
    private readonly ILogger<BackendBootstrapper> _logger;
    private readonly List<Task> _fanTasks = new();
    private readonly List<IHapticBackend> _registered = new();
    private readonly CancellationTokenSource _stopping = new();

    public BackendBootstrapper(
        BackendRegistry registry,
        EventBus bus,
        IOptions<SmitedOptions> options,
        IServiceProvider services,
        ILogger<BackendBootstrapper> logger)
    {
        _registry = registry;
        _bus = bus;
        _options = options.Value;
        _services = services;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_options.Backends.EnableMockOwo)
        {
            var mock = _services.GetRequiredService<MockOwoBackend>();
            RegisterAndFan(mock);
            _logger.LogInformation("Registered backend {Id} ({Kind}: {DisplayName})",
                mock.Id, mock.Kind, mock.DisplayName);
        }

        if (_options.Backends.EnableOwo)
        {
            if (!OperatingSystem.IsWindows())
            {
                _logger.LogWarning("OWO backend disabled: EnableOwo=true but not running on Windows.");
            }
            else
            {
                var owoType = Type.GetType("Smited.Daemon.Owo.OwoBackend, Smited.Daemon.Owo");
                if (owoType is null)
                {
                    _logger.LogWarning(
                        "OWO backend disabled: EnableOwo=true but the Smited.Daemon.Owo assembly is not in the output directory.");
                }
                else
                {
                    var owo = (IHapticBackend)ActivatorUtilities.CreateInstance(_services, owoType);
                    RegisterAndFan(owo);
                    _logger.LogInformation("Registered Windows OWO backend {Id}", owo.Id);
                }
            }
        }

        return Task.CompletedTask;
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
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            // Don't block shutdown if a fan task is misbehaving.
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

    private void RegisterAndFan(IHapticBackend backend)
    {
        _registry.Register(backend);
        _registered.Add(backend);
        _fanTasks.Add(Task.Run(() => FanEventsAsync(backend, _stopping.Token)));
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

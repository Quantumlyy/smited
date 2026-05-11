using Microsoft.Extensions.DependencyInjection;
using Smited.Daemon.Admin.Services;

namespace Smited.Daemon.Admin;

/// <summary>
/// Registers Blazor Server services and admin-specific singletons + scoped
/// services. The daemon's existing services (<c>BackendRegistry</c>,
/// <c>TriggerCoordinator</c>, <c>EventBus</c>, <c>SensationLibrary</c>,
/// history factory) are already registered and are injected directly by
/// components.
/// </summary>
internal static class AdminConfiguration
{
    public static IServiceCollection AddSmitedAdmin(this IServiceCollection services)
    {
        services.AddRazorPages();
        services.AddServerSideBlazor();
        services.AddSingleton<PanicCounter>();
        // Transient: each component injects its own EventStreamSubscriber
        // so each gets its own underlying EventBus.Subscription. Channel
        // readers are single-consumer; sharing one subscription would
        // race components against each other for events.
        services.AddTransient<EventStreamSubscriber>();
        services.AddScoped<HistoryQueryService>();
        // BodyMapPageState is a singleton EventBus consumer driving the
        // /bodymap admin page. The hosted-service alias drives its
        // StartAsync/StopAsync lifecycle while the singleton lets
        // Blazor components inject the same instance to read state.
        services.AddSingleton<BodyMapPageState>();
        services.AddHostedService(sp => sp.GetRequiredService<BodyMapPageState>());
        return services;
    }
}

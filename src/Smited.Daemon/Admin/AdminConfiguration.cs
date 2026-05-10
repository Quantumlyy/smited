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
        services.AddScoped<EventStreamSubscriber>();
        services.AddScoped<HistoryQueryService>();
        return services;
    }
}

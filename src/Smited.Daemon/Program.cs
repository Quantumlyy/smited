using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProtoValidate;
using Serilog;
using Smited.Daemon.Admin;
using Smited.Daemon.Backends;
using Smited.Daemon.Backends.Mock;
using Smited.Daemon.BodyMap;
using Smited.Daemon.Configuration;
using Smited.Daemon.Diagnostics;
using Smited.Daemon.Events;
using Smited.Daemon.History;
using Smited.Daemon.Sensations;
using Smited.Daemon.Services;
using Smited.Daemon.Triggering;
using Smited.Daemon.Validation;
using Smited.V1;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

// User config layer: appsettings.json (+ Development overlay) is added by
// CreateBuilder; layering the per-user config file AFTER means user values
// win over daemon defaults. optional=true so missing file is fine;
// reloadOnChange=false because startup state doesn't reconcile mid-flight.
var userConfigPath = UserConfigPath.Resolve();
UserConfigPath.EnsureExists(userConfigPath);
builder.Configuration.AddJsonFile(userConfigPath, optional: true, reloadOnChange: false);

builder.Host.UseSerilog((ctx, cfg) => cfg.ReadFrom.Configuration(ctx.Configuration));

builder.Services.Configure<SmitedOptions>(builder.Configuration.GetSection("Smited"));

// Daemon-wide bHaptics settings. Distinct from per-backend options
// (BhapticsVestOptions etc.) because the bHaptics SDK is a process-wide
// singleton and its identity is decided once at first InitializeAsync —
// see BhapticsGlobalOptions remarks.
builder.Services.Configure<BhapticsGlobalOptions>(builder.Configuration.GetSection("Smited:Bhaptics"));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<EventBus>();
builder.Services.AddSingleton<IBackendEventSink>(sp => sp.GetRequiredService<EventBus>());
builder.Services.AddSingleton<EventStream>();
builder.Services.AddSingleton<BackendRegistry>();
builder.Services.AddSingleton<SensationLibrary>();
builder.Services.AddSingleton<ConcurrencyEnforcer>();
builder.Services.AddSingleton<TriggerCoordinator>();
builder.Services.AddSingleton<SmitedActionService>();
builder.Services.AddSingleton<DaemonStartTime>();

builder.Services.AddSingleton<MockOwoBackend>();
builder.Services.AddSingleton<IMockOwoController>(sp => sp.GetRequiredService<MockOwoBackend>());

// Mock bHaptics backends. Vest is a plain singleton; sleeve and feet
// are keyed by "left"/"right" so the factory can resolve the correct
// instance per descriptor kind. Tests resolve concrete types directly
// from DI (BhapticsE2ETests / DaemonFixture do this).
builder.Services.AddSingleton<MockBhapticsVestBackend>();
builder.Services.AddKeyedSingleton<MockBhapticsSleeveBackend>("left",
    (sp, _) => ActivatorUtilities.CreateInstance<MockBhapticsSleeveBackend>(sp, "left"));
builder.Services.AddKeyedSingleton<MockBhapticsSleeveBackend>("right",
    (sp, _) => ActivatorUtilities.CreateInstance<MockBhapticsSleeveBackend>(sp, "right"));
builder.Services.AddKeyedSingleton<MockBhapticsFeetBackend>("left",
    (sp, _) => ActivatorUtilities.CreateInstance<MockBhapticsFeetBackend>(sp, "left"));
builder.Services.AddKeyedSingleton<MockBhapticsFeetBackend>("right",
    (sp, _) => ActivatorUtilities.CreateInstance<MockBhapticsFeetBackend>(sp, "right"));

builder.Services.AddSingleton<BodyMapValidator>();
builder.Services.AddSingleton<BodyMapState>();
builder.Services.AddSingleton<IBodyMapState>(sp => sp.GetRequiredService<BodyMapState>());

// Cross-platform backend factory registrations.
builder.Services.AddSmitedBackends();

// Reflectively register OwoBackendFactory + StaticOwoSdk on Windows.
// No-op on non-Windows; the daemon stays up and OWO triggers are
// rejected as if the assembly were absent. The factory binds its
// OwoBackendOptions from the descriptor's Options sub-section, so
// the legacy SmitedOptions.Backends.Owo singleton registration is
// no longer needed.
builder.Services.AddOwoBackendIfWindows();

// Same shape for bHaptics: one factory class instance per supported
// kind (vest, sleeve_l/r, feet_l/r) gets registered on Windows hosts;
// all five share a single StaticBhapticsSdk singleton because the
// bHaptics Player is a per-host process owning every paired device.
builder.Services.AddBhapticsBackendIfWindows();

// History database (daemon-internal SQLite). Registered first so the
// schema is ready and the EventBus subscriber is attached BEFORE
// BackendBootstrapper publishes its initial registration events —
// otherwise the boot-time backend lifecycle rows would never be written.
var historyOptions = builder.Configuration.GetSection("Smited:History").Get<SmitedOptions.HistoryOptions>()
    ?? new SmitedOptions.HistoryOptions();
var historyDbPath = HistoryDbPathResolver.Resolve(historyOptions.CustomPath);
builder.Services.AddSingleton(new HistoryDbPath(historyDbPath));

if (historyOptions.Enabled)
{
    Directory.CreateDirectory(Path.GetDirectoryName(historyDbPath)!);
    builder.Services.AddDbContextFactory<HistoryDbContext>(opts =>
        opts.UseSqlite($"Data Source={historyDbPath}"));
    builder.Services.AddSingleton<IHistoryRecorder, HistoryRecorder>();
    builder.Services.AddHostedService<HistoryDbInitializer>();
    builder.Services.AddHostedService<HistoryEventBusSubscriber>();
}
else
{
    builder.Services.AddSingleton<IHistoryRecorder, NullHistoryRecorder>();
}

// Order matters: BackendBootstrapper runs after history subscriber attaches
// so the BackendLifecycleEvent fired on registration is captured. Sensation
// loader runs last so it sees a populated BackendRegistry.
builder.Services.AddHostedService<BackendBootstrapper>();
builder.Services.AddHostedService<SensationLoader>();

// Retention runs last — non-critical background pruning.
if (historyOptions.Enabled)
{
    builder.Services.AddHostedService<HistoryRetentionService>();
}

builder.Services.AddProtoValidate(opts =>
{
    opts.PreLoadDescriptors = true;
    opts.FileDescriptors = new List<Google.Protobuf.Reflection.FileDescriptor>
    {
        SmitedReflection.Descriptor,
    };
});
builder.Services.AddSingleton<ProtovalidateInterceptor>();

builder.Services.AddGrpc(o =>
{
    o.Interceptors.Add<ProtovalidateInterceptor>();
});
builder.Services.AddGrpcReflection();

builder.Services.AddSmitedAdmin();

builder.WebHost.ConfigureKestrel(o =>
{
    var smited = builder.Configuration.GetSection("Smited").Get<SmitedOptions>() ?? new SmitedOptions();
    var bind = IPAddress.Parse(smited.BindAddress);

    // gRPC over h2c
    o.Listen(bind, smited.GrpcPort, lo => lo.Protocols = HttpProtocols.Http2);

    // Emergency-stop endpoint over HTTP/1.1 — separate listener so a wedged
    // gRPC pipeline can't take the panic button down with it.
    o.Listen(bind, smited.PanicPort, lo => lo.Protocols = HttpProtocols.Http1);

    // Admin UI over HTTP/1.1 — separate listener so the admin port can be
    // bound to 127.0.0.1 even when gRPC is opened to the LAN.
    if (smited.Admin.Enabled)
    {
        var adminBind = IPAddress.Parse(smited.Admin.BindAddress);
        o.Listen(adminBind, smited.Admin.Port, lo => lo.Protocols = HttpProtocols.Http1);
    }
});

var app = builder.Build();

app.MapGrpcService<SmitedGrpcService>();
if (app.Configuration.GetValue<bool>("Smited:EnableReflection"))
{
    app.MapGrpcReflectionService();
}
app.MapPanic();

// Admin UI pipeline gated to its own port so gRPC and panic stay isolated
// from Blazor's HTTP/1.1 routing, static-file middleware, and SignalR hub.
{
    var adminOpts = app.Services.GetRequiredService<IOptions<SmitedOptions>>().Value.Admin;
    if (adminOpts.Enabled)
    {
        var adminPort = adminOpts.Port;
        app.MapWhen(ctx => ctx.Connection.LocalPort == adminPort, branch =>
        {
            branch.UseStaticFiles();
            branch.UseRouting();
            branch.UseEndpoints(endpoints =>
            {
                endpoints.MapBlazorHub();
                endpoints.MapFallbackToPage("/_Host");
            });
        });
    }
}

var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
lifetime.ApplicationStarted.Register(() =>
{
    var opts = app.Services.GetRequiredService<IOptions<SmitedOptions>>().Value;
    var registry = app.Services.GetRequiredService<BackendRegistry>();
    var library = app.Services.GetRequiredService<SensationLibrary>();
    var dbPath = opts.History.Enabled
        ? app.Services.GetRequiredService<HistoryDbPath>().Value
        : null;
    var bodyMapState = app.Services.GetRequiredService<IBodyMapState>();
    StartupBanner.Render(opts, registry.Count, library.Count, dbPath, bodyMapState);
});

await app.RunAsync();

/// <summary>
/// Public partial declaration so <c>WebApplicationFactory&lt;Program&gt;</c>
/// in the test project can reach the host.
/// </summary>
public partial class Program;

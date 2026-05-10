using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProtoValidate;
using Serilog;
using Smited.Daemon.Backends;
using Smited.Daemon.Backends.Mock;
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

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<EventBus>();
builder.Services.AddSingleton<IBackendEventSink>(sp => sp.GetRequiredService<EventBus>());
builder.Services.AddSingleton<EventStream>();
builder.Services.AddSingleton<BackendRegistry>();
builder.Services.AddSingleton<SensationLibrary>();
builder.Services.AddSingleton<ConcurrencyEnforcer>();
builder.Services.AddSingleton<TriggerCoordinator>();
builder.Services.AddSingleton<DaemonStartTime>();

builder.Services.AddSingleton<MockOwoBackend>();
builder.Services.AddSingleton<IMockOwoController>(sp => sp.GetRequiredService<MockOwoBackend>());

// OwoBackend (when enabled) is loaded reflectively in BackendBootstrapper and
// constructed via ActivatorUtilities.CreateInstance, which resolves its
// OwoBackendOptions parameter from the container. Surfacing the nested options
// here lets the OWO backend stay decoupled from SmitedOptions/IOptions<T>.
builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<IOptions<SmitedOptions>>().Value.Backends.Owo);

// IOwoSdk is registered only on Windows + EnableOwo because StaticOwoSdk
// imports the OWOGame namespace, which is only present in the Windows-only
// OWO NuGet package. The implementation is loaded reflectively so this
// project doesn't need a compile-time reference to Smited.Daemon.Owo on
// Mac/Linux. The Type.GetType call is wrapped because — even with
// throwOnError=false (the default) — file-load failures surface here:
// if Smited.Daemon.Owo.dll is present but its OWO.dll runtime dependency
// is missing or unloadable, the lookup throws FileNotFoundException /
// FileLoadException / TypeLoadException. Crashing daemon startup on that
// path defeats the point of the reflective load; we log via Serilog (the
// host logger isn't constructed yet but Serilog's static API is wired
// from configuration earlier) and skip registration. BackendBootstrapper's
// own reflective lookup of OwoBackend will then log the user-facing
// "EnableOwo set but assembly missing" warning when it hits the same
// resolution.
if (OperatingSystem.IsWindows())
{
    var enableOwo = builder.Configuration.GetValue<bool>("Smited:Backends:EnableOwo");
    if (enableOwo)
    {
        Type? staticSdkType = null;
        try
        {
            staticSdkType = Type.GetType("Smited.Daemon.Owo.StaticOwoSdk, Smited.Daemon.Owo");
        }
        catch (Exception ex)
        {
            // Serilog's host pipeline isn't online yet (UseSerilog is
            // configured but the host hasn't started), so the static
            // Log.Logger would no-op here. Console.Error is the
            // pre-host-start signal channel; BackendBootstrapper will
            // also log a structured warning later when its own
            // reflective lookup runs.
            Console.Error.WriteLine(
                "warn: Skipping IOwoSdk registration; reflective load of "
                + "Smited.Daemon.Owo.StaticOwoSdk threw. The daemon will "
                + "still run; OWO triggers will be rejected as if the "
                + $"assembly were absent. Underlying error: {ex.GetType().Name}: {ex.Message}");
        }

        if (staticSdkType is not null)
        {
            builder.Services.AddSingleton(typeof(IOwoSdk), staticSdkType);
        }
    }
}

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

builder.WebHost.ConfigureKestrel(o =>
{
    var smited = builder.Configuration.GetSection("Smited").Get<SmitedOptions>() ?? new SmitedOptions();
    var bind = IPAddress.Parse(smited.BindAddress);

    // gRPC over h2c
    o.Listen(bind, smited.GrpcPort, lo => lo.Protocols = HttpProtocols.Http2);

    // Emergency-stop endpoint over HTTP/1.1 — separate listener so a wedged
    // gRPC pipeline can't take the panic button down with it.
    o.Listen(bind, smited.PanicPort, lo => lo.Protocols = HttpProtocols.Http1);
});

var app = builder.Build();

app.MapGrpcService<SmitedGrpcService>();
if (app.Configuration.GetValue<bool>("Smited:EnableReflection"))
{
    app.MapGrpcReflectionService();
}
app.MapPanic();

var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
lifetime.ApplicationStarted.Register(() =>
{
    var opts = app.Services.GetRequiredService<IOptions<SmitedOptions>>().Value;
    var registry = app.Services.GetRequiredService<BackendRegistry>();
    var library = app.Services.GetRequiredService<SensationLibrary>();
    var dbPath = opts.History.Enabled
        ? app.Services.GetRequiredService<HistoryDbPath>().Value
        : null;
    StartupBanner.Render(opts, registry.Count, library.Count, dbPath);
});

await app.RunAsync();

/// <summary>
/// Public partial declaration so <c>WebApplicationFactory&lt;Program&gt;</c>
/// in the test project can reach the host.
/// </summary>
public partial class Program;

using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using ProtoValidate;
using Serilog;
using Smited.Daemon.Backends;
using Smited.Daemon.Backends.Mock;
using Smited.Daemon.Configuration;
using Smited.Daemon.Diagnostics;
using Smited.Daemon.Events;
using Smited.Daemon.Sensations;
using Smited.Daemon.Services;
using Smited.Daemon.Triggering;
using Smited.Daemon.Validation;
using Smited.V1;

var builder = WebApplication.CreateBuilder(args);

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

// Order matters: BackendBootstrapper runs first so SensationLoader sees a
// populated BackendRegistry. IHostedService.StartAsync invokes services
// in the order they were registered.
builder.Services.AddHostedService<BackendBootstrapper>();
builder.Services.AddHostedService<SensationLoader>();

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
    var smited = builder.Configuration.GetSection("Smited").Get<SmitedOptions>()!;
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
    StartupBanner.Render(opts, registry.Count, library.Count);
});

await app.RunAsync();

/// <summary>
/// Public partial declaration so <c>WebApplicationFactory&lt;Program&gt;</c>
/// in the test project can reach the host.
/// </summary>
public partial class Program;

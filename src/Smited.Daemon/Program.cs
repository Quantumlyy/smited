using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Serilog;
using Smited.Daemon.Configuration;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, cfg) => cfg.ReadFrom.Configuration(ctx.Configuration));

builder.Services.Configure<SmitedOptions>(builder.Configuration.GetSection("Smited"));

builder.Services.AddGrpc();
builder.Services.AddGrpcReflection();

builder.WebHost.ConfigureKestrel(o =>
{
    var smited = builder.Configuration.GetSection("Smited").Get<SmitedOptions>()!;
    var bind = IPAddress.Parse(smited.BindAddress);

    o.Listen(bind, smited.GrpcPort, lo => lo.Protocols = HttpProtocols.Http2);
    o.Listen(bind, smited.PanicPort, lo => lo.Protocols = HttpProtocols.Http1);
});

var app = builder.Build();

if (app.Configuration.GetValue<bool>("Smited:EnableReflection"))
{
    app.MapGrpcReflectionService();
}

await app.RunAsync();

/// <summary>
/// Public partial declaration so <c>WebApplicationFactory&lt;Program&gt;</c>
/// in the test project can reach the host.
/// </summary>
public partial class Program;

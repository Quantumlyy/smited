using System.Net;
using System.Net.Http.Headers;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Smited.Daemon.Backends;
using Smited.Daemon.Backends.Mock;
using Smited.Daemon.Events;
using Smited.Daemon.Sensations;
using Smited.V1;

namespace Smited.Daemon.Tests.Fixtures;

/// <summary>
/// In-process daemon for end-to-end gRPC + HTTP tests. Boots the host
/// via <see cref="WebApplicationFactory{TEntryPoint}"/>, swaps the system
/// clock for <see cref="FakeTimeProvider"/>, points the sensation library
/// at a per-fixture temp directory, and exposes typed clients for both
/// transports. Both gRPC and the <c>/panic</c> endpoint share the
/// in-memory pipeline; the test server routes by URL path so the port
/// numbers in <c>SmitedOptions</c> are inert in tests.
/// </summary>
internal sealed class DaemonFixture : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _libraryRoot;
    private readonly string _userConfigDir;
    private readonly string? _previousUserConfigDir;

    public DaemonFixture(Action<string>? seed = null)
    {
        _libraryRoot = Path.Combine(Path.GetTempPath(), "smited-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_libraryRoot);
        Directory.CreateDirectory(Path.Combine(_libraryRoot, "owo_skin"));
        seed?.Invoke(_libraryRoot);

        // Redirect user config away from the real ~/.config/smited so the
        // test run doesn't touch the developer's actual directory.
        _userConfigDir = Path.Combine(_libraryRoot, "user-config");
        _previousUserConfigDir = Environment.GetEnvironmentVariable("SMITED_CONFIG_DIR");
        Environment.SetEnvironmentVariable("SMITED_CONFIG_DIR", _userConfigDir);

        Time = new FakeTimeProvider(new DateTimeOffset(2026, 5, 9, 12, 0, 0, TimeSpan.Zero));

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Smited:GrpcPort"] = "0",
                        ["Smited:PanicPort"] = "0",
                        ["Smited:BindAddress"] = "127.0.0.1",
                        ["Smited:Sensations:LibraryRoot"] = _libraryRoot,
                        ["Smited:Backends:EnableMockOwo"] = "true",
                        ["Smited:Backends:EnableOwo"] = "false",
                        ["Serilog:MinimumLevel"] = "Warning",
                    });
                });
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<TimeProvider>();
                    services.AddSingleton<TimeProvider>(Time);
                });
            });

        // Force the host to boot so backends register and sensations load
        // before the first test method touches the fixture.
        var handler = _factory.Server.CreateHandler();
        PanicHttpClient = new HttpClient(handler)
        {
            BaseAddress = _factory.Server.BaseAddress,
            DefaultRequestVersion = new Version(1, 1),
        };

        var grpcHandler = _factory.Server.CreateHandler();
        var grpcHttp = new HttpClient(new ForceHttp2Handler(grpcHandler))
        {
            BaseAddress = _factory.Server.BaseAddress,
            DefaultRequestVersion = new Version(2, 0),
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        Channel = GrpcChannel.ForAddress(_factory.Server.BaseAddress, new GrpcChannelOptions
        {
            HttpClient = grpcHttp,
        });
        Client = new SmitedService.SmitedServiceClient(Channel);
    }

    /// <summary>The shared <see cref="FakeTimeProvider"/>.</summary>
    public FakeTimeProvider Time { get; }

    /// <summary>gRPC client wired to the in-process server.</summary>
    public SmitedService.SmitedServiceClient Client { get; }

    /// <summary>HTTP/1.1 client for the <c>/panic</c> endpoint.</summary>
    public HttpClient PanicHttpClient { get; }

    public GrpcChannel Channel { get; }

    /// <summary>Backend registry the host populated at boot.</summary>
    public BackendRegistry Registry => _factory.Services.GetRequiredService<BackendRegistry>();

    /// <summary>Sensation library after boot-time loading.</summary>
    public SensationLibrary Library => _factory.Services.GetRequiredService<SensationLibrary>();

    /// <summary>The shared <see cref="EventBus"/> for direct subscribe/publish.</summary>
    public EventBus EventBus => _factory.Services.GetRequiredService<EventBus>();

    /// <summary>The mock backend's controller surface.</summary>
    public IMockOwoController MockController => _factory.Services.GetRequiredService<IMockOwoController>();

    public string LibraryRoot => _libraryRoot;

    public void Dispose()
    {
        try { Channel.Dispose(); } catch { }
        try { PanicHttpClient.Dispose(); } catch { }
        try { _factory.Dispose(); } catch { }
        Environment.SetEnvironmentVariable("SMITED_CONFIG_DIR", _previousUserConfigDir);
        try
        {
            if (Directory.Exists(_libraryRoot))
            {
                Directory.Delete(_libraryRoot, recursive: true);
            }
        }
        catch { }
    }

    /// <summary>
    /// TestServer's response.Version isn't always set to 2.0 even when the
    /// request used HTTP/2; the gRPC client requires Version=2.0 to read
    /// trailers, so this handler patches the response to satisfy that.
    /// </summary>
    private sealed class ForceHttp2Handler : DelegatingHandler
    {
        public ForceHttp2Handler(HttpMessageHandler inner) : base(inner) { }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            request.Version = new Version(2, 0);
            request.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
            var response = await base.SendAsync(request, ct).ConfigureAwait(false);
            response.Version = new Version(2, 0);
            return response;
        }
    }
}

using System.Net;
using System.Net.Http.Headers;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Smited.Daemon.Backends;
using Smited.Daemon.Backends.Mock;
using Smited.Daemon.BodyMap;
using Smited.Daemon.Events;
using Smited.Daemon.History;
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
    private readonly bool _ownsLibraryRoot;
    private readonly string _userConfigDir;
    private readonly string? _previousUserConfigDir;

    public DaemonFixture(
        Action<string>? seed = null,
        Action<IServiceCollection>? configureServices = null,
        string? libraryRoot = null,
        IReadOnlyDictionary<string, string?>? additionalConfig = null)
    {
        _libraryRoot = libraryRoot ?? Path.Combine(Path.GetTempPath(), "smited-fixture-" + Guid.NewGuid().ToString("N"));
        _ownsLibraryRoot = libraryRoot is null;
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
                    // The fixture intentionally does NOT pre-populate
                    // Smited:Backends:Items here. The bootstrapper's
                    // empty-Items fallback synthesizes a default mock-owo
                    // descriptor at startup, which gives every E2E test
                    // mock-owo for free without the fixture leaking a
                    // shape that the production daemon doesn't ship in
                    // appsettings.json. Tests that need additional or
                    // alternative descriptors layer them on via
                    // `additionalConfig`.
                    var baseConfig = new Dictionary<string, string?>
                    {
                        ["Smited:GrpcPort"] = "0",
                        ["Smited:PanicPort"] = "0",
                        ["Smited:BindAddress"] = "127.0.0.1",
                        ["Smited:Sensations:LibraryRoot"] = _libraryRoot,
                        ["Smited:History:Enabled"] = "true",
                        ["Smited:History:CustomPath"] = Path.Combine(_libraryRoot, "history.db"),
                        ["Serilog:MinimumLevel"] = "Warning",
                    };
                    if (additionalConfig is not null)
                    {
                        foreach (var (key, value) in additionalConfig)
                        {
                            baseConfig[key] = value;
                        }
                    }
                    config.AddInMemoryCollection(baseConfig);
                });
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<TimeProvider>();
                    services.AddSingleton<TimeProvider>(Time);
                    configureServices?.Invoke(services);
                });
            });

        // Force the host to boot so backends register and sensations load
        // before the first test method touches the fixture. Wrapped in
        // try/catch so a boot failure (e.g. invalid descriptor config)
        // doesn't leak the env-var override or the temp library root —
        // the test that intentionally provokes the failure asserts on
        // the exception and then xunit moves on without ever calling
        // Dispose.
        try
        {
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
        catch
        {
            CleanUpAfterFailedBoot();
            throw;
        }
    }

    private void CleanUpAfterFailedBoot()
    {
        try { _factory.Dispose(); } catch { }
        Environment.SetEnvironmentVariable("SMITED_CONFIG_DIR", _previousUserConfigDir);
        try
        {
            if (_ownsLibraryRoot && Directory.Exists(_libraryRoot))
            {
                Directory.Delete(_libraryRoot, recursive: true);
            }
        }
        catch { }
    }

    /// <summary>The shared <see cref="FakeTimeProvider"/>.</summary>
    public FakeTimeProvider Time { get; }

    /// <summary>
    /// Root <see cref="IServiceProvider"/> for tests that need to resolve
    /// services not exposed via dedicated typed properties.
    /// </summary>
    public IServiceProvider Services => _factory.Services;

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

    /// <summary>
    /// Bodymap state populated by <c>BackendBootstrapper</c> after the
    /// validator runs. Exposes <see cref="IBodyMapState.RefusedBackendCount"/>
    /// (the value the startup banner reads), <see cref="IBodyMapState.PlacementCount"/>,
    /// and <see cref="IBodyMapState.WarningCount"/>.
    /// </summary>
    public IBodyMapState BodyMapState => _factory.Services.GetRequiredService<IBodyMapState>();

    /// <summary>
    /// Factory for the in-process SQLite history database, so tests can
    /// query the rows the daemon wrote.
    /// </summary>
    public IDbContextFactory<HistoryDbContext> HistoryFactory =>
        _factory.Services.GetRequiredService<IDbContextFactory<HistoryDbContext>>();

    public string LibraryRoot => _libraryRoot;

    public void Dispose()
    {
        try { Channel.Dispose(); } catch { }
        try { PanicHttpClient.Dispose(); } catch { }
        try { _factory.Dispose(); } catch { }
        Environment.SetEnvironmentVariable("SMITED_CONFIG_DIR", _previousUserConfigDir);
        try
        {
            if (_ownsLibraryRoot && Directory.Exists(_libraryRoot))
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

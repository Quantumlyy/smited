using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Smited.Daemon.Backends;
using Smited.Daemon.Configuration;
using MicrosensationParameters = Smited.Daemon.Backends.Internal.MicrosensationParameters;

namespace Smited.Daemon.Sensations;

/// <summary>
/// Walks <c>Smited:Sensations:LibraryRoot</c> at startup and inserts every
/// sensation file into the <see cref="SensationLibrary"/>, validating each
/// file against the backend's parameter schema and zone topology before
/// registering. Validation errors abort the host with the file path and
/// offending field in the exception message.
/// </summary>
internal sealed class SensationLoader : IHostedService
{
    private readonly BackendRegistry _registry;
    private readonly SensationLibrary _library;
    private readonly TimeProvider _time;
    private readonly SmitedOptions _options;
    private readonly ILogger<SensationLoader> _logger;

    public SensationLoader(
        BackendRegistry registry,
        SensationLibrary library,
        TimeProvider time,
        IOptions<SmitedOptions> options,
        ILogger<SensationLoader> logger)
    {
        _registry = registry;
        _library = library;
        _time = time;
        _options = options.Value;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var root = ResolveLibraryRoot(_options.Sensations.LibraryRoot);
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            _logger.LogInformation(
                "Sensation library root '{Root}' does not exist; skipping load.", root);
            return Task.CompletedTask;
        }

        var byKind = _registry.All
            .GroupBy(b => b.Kind, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);

        foreach (var (kind, backends) in byKind)
        {
            var subdir = Path.Combine(root, kind);
            if (!Directory.Exists(subdir))
            {
                _logger.LogDebug(
                    "No sensations directory for backend kind '{Kind}' at '{Subdir}'.",
                    kind, subdir);
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(subdir, "*.json"))
            {
                LoadFile(path, kind, backends);
            }
        }

        _logger.LogInformation(
            "Sensation library loaded: {Count} entries across {BackendCount} backend(s).",
            _library.Count, _registry.Count);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static string ResolveLibraryRoot(string configured) =>
        LibraryRootResolver.Resolve(configured);

    private void LoadFile(string path, string expectedKind, IReadOnlyList<IHapticBackend> backends)
    {
        SensationFileDto file;
        try
        {
            file = SensationFileSerializer.Deserialize(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            throw new SmitedStartupException(
                $"Failed to parse sensation file '{path}': {ex.Message}", ex);
        }

        if (!string.Equals(file.BackendKind, expectedKind, StringComparison.OrdinalIgnoreCase))
        {
            throw new SmitedStartupException(
                $"Sensation file '{path}' declares backend_kind='{file.BackendKind}' but is in the '{expectedKind}' directory.");
        }

        ValidateFileLevel(path, file);

        foreach (var backend in backends)
        {
            ValidateAgainstBackend(path, file, backend);
            var sensation = SensationFileSerializer.ToInternal(file, backend.Id, _time.GetUtcNow());
            if (!_library.Register(sensation, overwrite: false))
            {
                throw new SmitedStartupException(
                    $"Sensation '{file.Name}' already registered for backend '{backend.Id}' before file '{path}' was loaded.");
            }
        }
    }

    /// <summary>
    /// Validates fields whose contract is independent of any specific
    /// backend's schema: shape of the JSON, required fields present,
    /// numeric fields within the schema-mandated ranges. Catching these
    /// at boot mirrors the validation that <c>protovalidate</c> applies
    /// to runtime <c>RegisterSensation</c> RPCs, so authored files can't
    /// quietly carry shapes that the wire would reject.
    /// </summary>
    private static void ValidateFileLevel(string path, SensationFileDto file)
    {
        if (string.IsNullOrEmpty(file.Name))
        {
            throw new SmitedStartupException($"Sensation file '{path}' has empty name.");
        }
        if (string.IsNullOrEmpty(file.DisplayName))
        {
            throw new SmitedStartupException($"Sensation file '{path}' has empty display_name.");
        }
        if (file.Definition.Microsensations.Count == 0)
        {
            throw new SmitedStartupException(
                $"Sensation file '{path}' has empty definition.microsensations (must contain at least one).");
        }
        if (file.DefaultIntensity is { } intensity && intensity > 100)
        {
            throw new SmitedStartupException(
                $"Sensation file '{path}' has default_intensity={intensity}; valid range is 0..100.");
        }
        if (file.EstimatedDuration < TimeSpan.Zero || file.EstimatedDuration > TimeSpan.FromMinutes(5))
        {
            throw new SmitedStartupException(
                $"Sensation file '{path}' has estimated_duration={file.EstimatedDuration}; valid range is 0s..300s.");
        }
    }

    private static void ValidateAgainstBackend(string path, SensationFileDto file, IHapticBackend backend)
    {
        var microsensations = file.Definition.Microsensations
            .Select(m => new MicrosensationParameters(m.Parameters))
            .ToArray();

        var failure = SensationValidator.Validate(microsensations, file.DefaultZoneIds, backend);
        if (failure is not null)
        {
            throw new SmitedStartupException(
                $"In '{path}': {failure.Value.Field} — {failure.Value.Message}.");
        }
    }
}

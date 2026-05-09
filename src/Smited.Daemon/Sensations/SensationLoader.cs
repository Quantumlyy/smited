using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Smited.Daemon.Backends;
using Smited.Daemon.Configuration;
using Smited.V1;
using ParameterValue = Smited.Daemon.Backends.Internal.ParameterValue;
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

    /// <summary>
    /// Resolves the configured library root: absolute paths pass through,
    /// relative paths are anchored to <see cref="AppContext.BaseDirectory"/>
    /// so the daemon's default <c>./sensations</c> resolves to the
    /// directory next to the binary regardless of the invoker's working
    /// directory.
    /// </summary>
    private static string ResolveLibraryRoot(string configured)
    {
        if (string.IsNullOrEmpty(configured) || Path.IsPathRooted(configured))
        {
            return configured;
        }
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configured));
    }

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

        if (string.IsNullOrEmpty(file.Name))
        {
            throw new SmitedStartupException(
                $"Sensation file '{path}' has empty name.");
        }

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

    private static void ValidateAgainstBackend(string path, SensationFileDto file, IHapticBackend backend)
    {
        var schema = backend.Parameters;
        var paramByName = schema.Parameters.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < file.Definition.Microsensations.Count; i++)
        {
            var micro = file.Definition.Microsensations[i];
            foreach (var (key, value) in micro.Parameters)
            {
                if (!paramByName.TryGetValue(key, out var def))
                {
                    throw new SmitedStartupException(
                        $"In '{path}': microsensations[{i}].parameters.{key} is not declared by backend '{backend.Id}'.");
                }
                if (!ValueMatchesType(value, def))
                {
                    throw new SmitedStartupException(
                        $"In '{path}': microsensations[{i}].parameters.{key} has wrong value type for parameter type {def.Type}.");
                }
                if (!ValueWithinRange(value, def, out var rangeError))
                {
                    throw new SmitedStartupException(
                        $"In '{path}': microsensations[{i}].parameters.{key} is out of range — {rangeError}.");
                }
            }

            foreach (var def in schema.Parameters)
            {
                if (def.Required && !micro.Parameters.ContainsKey(def.Name))
                {
                    throw new SmitedStartupException(
                        $"In '{path}': microsensations[{i}].parameters is missing required '{def.Name}' (backend '{backend.Id}').");
                }
            }
        }

        var knownZones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var z in backend.Zones.Zones)
        {
            knownZones.Add(z.Id);
        }
        foreach (var g in backend.Zones.Groups)
        {
            knownZones.Add(g.Id);
        }
        foreach (var zone in file.DefaultZoneIds)
        {
            if (!knownZones.Contains(zone))
            {
                throw new SmitedStartupException(
                    $"In '{path}': default_zone_ids contains '{zone}', not present on backend '{backend.Id}'.");
            }
        }
    }

    private static bool ValueMatchesType(ParameterValue value, ParameterDef def) => def.Type switch
    {
        ParameterType.Number => value is ParameterValue.Number,
        ParameterType.Bool => value is ParameterValue.Bool,
        ParameterType.String => value is ParameterValue.Text,
        ParameterType.Duration => value is ParameterValue.Duration,
        ParameterType.Enum => value is ParameterValue.EnumValue,
        _ => false,
    };

    private static bool ValueWithinRange(ParameterValue value, ParameterDef def, out string? error)
    {
        error = null;
        switch (value)
        {
            case ParameterValue.Number n:
                if (def.HasMin && n.Value < def.Min) { error = $"{n.Value} < min {def.Min}"; return false; }
                if (def.HasMax && n.Value > def.Max) { error = $"{n.Value} > max {def.Max}"; return false; }
                break;
            case ParameterValue.Duration d:
                var seconds = d.Value.TotalSeconds;
                if (def.HasMin && seconds < def.Min) { error = $"{seconds}s < min {def.Min}s"; return false; }
                if (def.HasMax && seconds > def.Max) { error = $"{seconds}s > max {def.Max}s"; return false; }
                break;
            case ParameterValue.EnumValue e:
                if (def.EnumValues.Count > 0 && !def.EnumValues.Contains(e.Value))
                {
                    error = $"'{e.Value}' is not in enum_values [{string.Join(", ", def.EnumValues)}]";
                    return false;
                }
                break;
        }
        return true;
    }
}

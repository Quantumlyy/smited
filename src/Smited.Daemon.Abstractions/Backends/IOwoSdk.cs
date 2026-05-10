namespace Smited.Daemon.Backends;

/// <summary>
/// Substitutable wrapper over the OWO C# SDK's static surface
/// (<c>OWOGame.OWO</c>). Lives in the abstractions assembly so the
/// daemon host can register an implementation cross-platform — the real
/// <c>StaticOwoSdk</c> impl is Windows-only and loaded reflectively, but
/// fakes used by tests can be wired anywhere.
/// </summary>
/// <remarks>
/// Method shapes use only primitive types and SDK-free records so test
/// projects targeting <c>net9.0</c> can substitute this interface
/// without taking a dependency on the <c>net9.0-windows</c> OWO package.
/// </remarks>
public interface IOwoSdk
{
    /// <summary>
    /// Establishes the SDK identity for this process. Maps the supplied
    /// <paramref name="projectId"/> onto the SDK's <c>GameAuth.WithId</c>
    /// so multi-app MyOWO setups can disambiguate. Idempotent — safe to
    /// call again on reconnect.
    /// </summary>
    void Configure(string projectId);

    /// <summary>
    /// Connect to the MyOWO app at the given IP. Throws on transport
    /// failure; the caller should react by transitioning to
    /// <c>BackendStatus.Error</c>.
    /// </summary>
    Task ConnectAsync(string ip);

    /// <summary>
    /// Auto-discover and connect to any MyOWO instance on the local
    /// network. Pairing requires the user to pick the daemon's entry in
    /// MyOWO's "Scan Games" panel — this method may block for many
    /// seconds while that handshake completes.
    /// </summary>
    Task AutoConnectAsync();

    /// <summary>
    /// Reports whether the SDK currently believes it has an active
    /// connection to a MyOWO instance.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Translate <paramref name="command"/> into an SDK
    /// <c>Sensation</c> attached to the resolved muscles and fire it.
    /// </summary>
    void Send(OwoSendCommand command);

    /// <summary>
    /// Cancel the currently-playing sensation. The OWO SDK only knows
    /// of a single active sensation per process, so this is a global
    /// stop — appropriate given OWO's exclusive concurrency model.
    /// </summary>
    void Stop();

    /// <summary>
    /// Tear down the SDK's connection to MyOWO. Called from the
    /// backend's <c>DisposeAsync</c>.
    /// </summary>
    void Disconnect();
}

/// <summary>
/// Backend-agnostic representation of one microsensation plus the
/// zones it should fire on. The OWO-specific translation
/// (<c>SensationsFactory.Create</c> + <c>WithMuscles</c>) lives inside
/// <c>StaticOwoSdk</c> so this record stays free of SDK types.
/// </summary>
/// <remarks>
/// All time fields are <strong>seconds</strong>. The OWO SDK's
/// <c>SensationsFactory.Create(...)</c> takes seconds for both ramps,
/// despite some older docs labelling them as milliseconds — the
/// implementation here matches the v2.4.x signature.
/// </remarks>
public sealed record OwoSendCommand(
    float FrequencyHz,
    float DurationSeconds,
    float IntensityPercentage,
    float RampUpSeconds,
    float RampDownSeconds,
    float ExitDelaySeconds,
    IReadOnlyList<string> ZoneIds);

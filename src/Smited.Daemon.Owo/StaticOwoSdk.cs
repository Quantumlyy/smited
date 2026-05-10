// Excluded from compile on non-Windows hosts because it imports the
// OWOGame namespace from the Windows-only OWO NuGet package. Mac builds
// don't restore the package and don't reference this project from the
// daemon, so excluding the file keeps the OWO csproj building to an
// empty assembly cross-platform.

#if WINDOWS
using OWOGame;
using Smited.Daemon.Backends;

namespace Smited.Daemon.Owo;

/// <summary>
/// Production <see cref="IOwoSdk"/> impl forwarding to <c>OWOGame.OWO</c>.
/// </summary>
/// <remarks>
/// The SDK's static API makes it impossible to substitute in tests
/// directly, which is the entire reason <see cref="IOwoSdk"/> exists.
/// This class is the single place the daemon talks to <c>OWOGame</c>;
/// any new SDK capability we expose to the rest of the daemon should
/// land here as a thin forward, not be called from elsewhere.
/// </remarks>
public sealed class StaticOwoSdk : IOwoSdk
{
    /// <inheritdoc />
    public void Configure(string projectId)
    {
        // GameAuth.Create() with no baked sensations is the right
        // factory for the dynamic-sensation flow smited uses; .WithId
        // tags the daemon for MyOWO's connection list. The SDK accepts
        // arbitrary strings here.
        var auth = GameAuth.Create().WithId(projectId);
        OWO.Configure(auth);
    }

    /// <inheritdoc />
    public Task ConnectAsync(string ip) => OWO.Connect(ip);

    /// <inheritdoc />
    public Task AutoConnectAsync() => OWO.AutoConnect();

    /// <inheritdoc />
    public bool IsConnected => OWO.IsConnected;

    /// <inheritdoc />
    public void Send(OwoSendCommand command)
    {
        // SDK signature (v2.4.x): SensationsFactory.Create takes all
        // floats — frequency in Hz, duration/exitDelay in seconds,
        // intensity as a 0..100 percentage, ramps in seconds.
        var sensation = SensationsFactory.Create(
            frequency: command.FrequencyHz,
            duration: command.DurationSeconds,
            intensity: command.IntensityPercentage,
            rampUp: command.RampUpSeconds,
            rampDown: command.RampDownSeconds,
            exitDelay: command.ExitDelaySeconds);

        var muscles = OwoMuscleMap.Resolve(command.ZoneIds);
        OWO.Send(sensation.WithMuscles(muscles));
    }

    /// <inheritdoc />
    public void Stop() => OWO.Stop();

    /// <inheritdoc />
    public void Disconnect() => OWO.Disconnect();
}
#endif

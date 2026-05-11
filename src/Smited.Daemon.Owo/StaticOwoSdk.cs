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
    public void Configure(string projectId, string? authString)
    {
        // Dev path (Visualizer) and production path (MyOWO consumer app)
        // diverge here:
        //   * GameAuth.Create() yields auth with no baked sensations.
        //     The OWO Visualizer accepts this, so it's enough for dev.
        //     MyOWO ignores unsigned auth and never lists the game in
        //     "Scan Games" — which is why the original implementation
        //     happened to work against the Visualizer but failed
        //     silently against MyOWO.
        //   * GameAuth.Parse(authString) yields auth with the baked
        //     sensations encoded in the .owoauth file from OWO's
        //     Sensations Creator tool. Required for the MyOWO consumer
        //     app per the OWO docs:
        //     https://owo-game.gitbook.io/owo-api/welcome/configure-your-project
        // Either way we end with .WithId(projectId) so the project ID
        // we advertise is the descriptor's, regardless of what the auth
        // string itself encodes.
        var auth = !string.IsNullOrEmpty(authString)
            ? GameAuth.Parse(authString).WithId(projectId)
            : GameAuth.Create().WithId(projectId);
        OWO.Configure(auth);
    }

    /// <inheritdoc />
    public Task ConnectAsync(string ip) => OWO.Connect(new[] { ip });

    /// <inheritdoc />
    public Task AutoConnectAsync() => OWO.AutoConnect();

    /// <inheritdoc />
    public bool IsConnected => OWO.ConnectionState == ConnectionState.Connected;

    /// <inheritdoc />
    public void Send(OwoSendCommand command)
    {
        // OWO SDK 2.4.x SensationsFactory.Create signature (verified by
        // reflecting the package's lib/net6.0/OWO.dll):
        //   MicroSensation Create(int frequency, float durationSeconds,
        //       int intensityPercentage, float rampUpMillis,
        //       float rampDownMillis, float exitDelaySeconds)
        // Positional dispatch sidesteps any parameter-name churn between
        // SDK versions.
        var sensation = SensationsFactory.Create(
            command.FrequencyHz,
            command.DurationSeconds,
            command.IntensityPercentage,
            command.RampUpSeconds,
            command.RampDownSeconds,
            command.ExitDelaySeconds);

        var muscles = OwoMuscleMap.Resolve(command.ZoneIds);
        OWO.Send(sensation, muscles);
    }

    /// <inheritdoc />
    public void Stop() => OWO.Stop();

    /// <inheritdoc />
    public void Disconnect() => OWO.Disconnect();
}
#endif

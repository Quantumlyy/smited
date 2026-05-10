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
    public void Send(OwoSendCommand command) =>
        throw new NotSupportedException(
            "StaticOwoSdk.Send is wired in commit O3 alongside OwoMuscleMap "
            + "and OwoBackend.TriggerAsync.");

    /// <inheritdoc />
    public void Stop() => OWO.Stop();

    /// <inheritdoc />
    public void Disconnect() => OWO.Disconnect();
}
#endif

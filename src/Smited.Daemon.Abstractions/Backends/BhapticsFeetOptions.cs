namespace Smited.Daemon.Backends;

/// <summary>
/// Configuration for the <c>bhaptics_feet_l</c> / <c>bhaptics_feet_r</c>
/// backends (Tactosy for Feet, 3 actuators per foot). The side is
/// intrinsic to the backend's <c>Kind</c> and is not part of this
/// type — see <see cref="BhapticsBackendOptionsBase"/> remarks.
/// </summary>
public sealed class BhapticsFeetOptions : BhapticsBackendOptionsBase
{
    public BhapticsFeetOptions()
    {
        BackendId = "bhaptics-feet";
    }
}

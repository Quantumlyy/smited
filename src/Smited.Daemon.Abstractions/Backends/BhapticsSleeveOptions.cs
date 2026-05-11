namespace Smited.Daemon.Backends;

/// <summary>
/// Configuration for the <c>bhaptics_sleeve_l</c> / <c>bhaptics_sleeve_r</c>
/// backends (TactSleeve, 6 actuators per arm). The side is intrinsic to
/// the backend's <c>Kind</c> and is not part of this type — see
/// <see cref="BhapticsBackendOptionsBase"/> remarks.
/// </summary>
public sealed class BhapticsSleeveOptions : BhapticsBackendOptionsBase
{
    public BhapticsSleeveOptions()
    {
        BackendId = "bhaptics-sleeve";
    }
}

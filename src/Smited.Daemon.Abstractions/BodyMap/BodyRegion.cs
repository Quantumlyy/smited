namespace Smited.Daemon.BodyMap;

/// <summary>
/// Discrete body regions used to declare backend zone placement and to
/// validate placements against backend-specific forbidden-region lists.
/// Deliberately coarse — roughly 30 regions covering the practical
/// addressable surface of haptic hardware — to keep configuration simple
/// and avoid the calibration headache that a coordinate-based system
/// would require.
/// </summary>
/// <remarks>
/// <para>
/// The taxonomy is fixed at the daemon level; users do not invent new
/// regions. When new backends require finer-grained placement (a future
/// TactFacial accessory, for example), the taxonomy is extended in a
/// versioned daemon release. The enum integer values are stable; new
/// values append at the end so configuration written against an older
/// daemon still binds against a newer one.
/// </para>
/// <para>
/// Some regions are sub-regions of others —
/// <see cref="ChestOverHeart"/> is a sub-region of
/// <see cref="ChestFront"/>, for example. The validator treats a
/// backend covering <see cref="ChestFront"/> as also covering
/// <see cref="ChestOverHeart"/> for forbidden-region checks; users
/// declaring a more specific region cannot sidestep broader bans by
/// nesting.
/// </para>
/// <para>
/// Lives in <c>Smited.Daemon.Abstractions</c> so backend assemblies
/// (including platform-conditional ones) can declare their
/// manufacturer-mandated <c>IHapticBackend.ForbiddenRegions</c>
/// without taking a dependency on the daemon host.
/// </para>
/// </remarks>
public enum BodyRegion
{
    /// <summary>
    /// Default value; the user has not declared a region for this
    /// placement. Treated as "not part of the body map" by the
    /// validator and overlap checks; backends with unspecified zones
    /// are not subject to overlap rejection.
    /// </summary>
    Unspecified = 0,

    /// <summary>Skull, scalp, ears. Adjacent to but not including <see cref="Face"/>.</summary>
    Head,

    /// <summary>
    /// Front of the head: forehead, eyes, nose, cheeks, mouth, jaw.
    /// In <c>SmitedDefaultForbiddenRegions</c> by default; overridable
    /// only when the user explicitly opts in.
    /// </summary>
    Face,

    /// <summary>
    /// Anterior neck (larynx, carotid artery zone). In
    /// <c>SmitedDefaultForbiddenRegions</c> by default — high vagal
    /// response risk. Distinct from <see cref="Neck"/>.
    /// </summary>
    Throat,

    /// <summary>Posterior and lateral neck, excluding <see cref="Throat"/>.</summary>
    Neck,

    /// <summary>
    /// Anterior chest, both sides — pectorals, sternum, upper rib
    /// cage. Contains <see cref="ChestOverHeart"/> as a sub-region;
    /// declaring a backend in <see cref="ChestFront"/> implicitly
    /// covers <see cref="ChestOverHeart"/>.
    /// </summary>
    ChestFront,

    /// <summary>
    /// Left-pectoral region overlying the heart. Sub-region of
    /// <see cref="ChestFront"/>. In <c>SmitedDefaultForbiddenRegions</c>
    /// by default — cardiac risk.
    /// </summary>
    ChestOverHeart,

    /// <summary>Upper abdominals: epigastric region, lower ribs.</summary>
    AbdomenUpper,

    /// <summary>Lower abdominals: hypogastric region, navel area.</summary>
    AbdomenLower,

    /// <summary>
    /// Pelvic region: groin, hips, lower torso. In
    /// <c>SmitedDefaultForbiddenRegions</c> by default; overridable.
    /// </summary>
    Pelvis,

    /// <summary>Upper back: shoulder blades, upper spine area.</summary>
    BackUpper,

    /// <summary>Lower back: lumbar region.</summary>
    BackLower,

    /// <summary>Buttocks. Distinct from <see cref="Pelvis"/>.</summary>
    Glutes,

    /// <summary>Left shoulder: deltoid, acromion area.</summary>
    LeftShoulder,
    /// <summary>Right shoulder: deltoid, acromion area.</summary>
    RightShoulder,

    /// <summary>Left upper arm: biceps, triceps.</summary>
    LeftUpperArm,
    /// <summary>Right upper arm: biceps, triceps.</summary>
    RightUpperArm,

    /// <summary>Left forearm: from elbow to wrist.</summary>
    LeftForearm,
    /// <summary>Right forearm: from elbow to wrist.</summary>
    RightForearm,

    /// <summary>Left wrist.</summary>
    LeftWrist,
    /// <summary>Right wrist.</summary>
    RightWrist,

    /// <summary>Left hand: palm, back of hand, fingers.</summary>
    LeftHand,
    /// <summary>Right hand: palm, back of hand, fingers.</summary>
    RightHand,

    /// <summary>Left thigh: quadriceps, hamstrings.</summary>
    LeftThigh,
    /// <summary>Right thigh: quadriceps, hamstrings.</summary>
    RightThigh,

    /// <summary>Left knee: patella and surrounding tissue.</summary>
    LeftKnee,
    /// <summary>Right knee: patella and surrounding tissue.</summary>
    RightKnee,

    /// <summary>Left calf: gastrocnemius, soleus.</summary>
    LeftCalf,
    /// <summary>Right calf: gastrocnemius, soleus.</summary>
    RightCalf,

    /// <summary>Left ankle.</summary>
    LeftAnkle,
    /// <summary>Right ankle.</summary>
    RightAnkle,

    /// <summary>Left foot: top, sole, toes.</summary>
    LeftFoot,
    /// <summary>Right foot: top, sole, toes.</summary>
    RightFoot,
}

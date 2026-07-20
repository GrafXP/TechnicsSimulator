using System.Collections.Immutable;
using System.Numerics;

namespace TechnicsSim.Mechanics.Catalog;

/// <summary>
/// What a part does mechanically. The shadow library describes connection geometry only, so
/// nothing here can be derived from snap metadata: tooth counts, pitch surfaces, motor outputs,
/// and clutch behaviour all have to be stated.
/// </summary>
public enum MechanicalComponentType
{
    /// <summary>Ordinary parallel-axis gear.</summary>
    SpurGear,

    /// <summary>Single-sided bevel, meshing with a perpendicular partner.</summary>
    BevelGear,

    /// <summary>Double-sided bevel, usable as either a spur or a bevel.</summary>
    DoubleBevelGear,

    /// <summary>Crown gear: spur teeth on the rim, perpendicular mesh on the face.</summary>
    CrownGear,

    /// <summary>Knob gear, meshing only with other knob gears.</summary>
    KnobGear,

    /// <summary>Worm, driving a perpendicular partner at starts/teeth.</summary>
    WormGear,

    /// <summary>Turntable ring. Rotary, but not a plain gear pair.</summary>
    Turntable,

    /// <summary>Linear toothed rack, converting rotation to translation.</summary>
    GearRack,

    /// <summary>Track sprocket.</summary>
    Sprocket,

    /// <summary>Gear with a torque-limiting clutch between teeth and axle hole.</summary>
    ClutchGear,

    /// <summary>One yoke of a universal joint.</summary>
    UniversalJointEnd,

    /// <summary>The cross/centre of a universal joint.</summary>
    UniversalJointCentre,

    /// <summary>Keyed rider that transfers axle rotation without gearing.</summary>
    AxleDriver,

    /// <summary>Gearbox selector that engages one of several paths.</summary>
    ChangeoverCatch,

    /// <summary>Linear actuator body, piston, or mount.</summary>
    LinearActuator,

    /// <summary>Differential housing. Unused by the supplied models, kept so one is classified rather than meshed.</summary>
    Differential,

    /// <summary>Powered input.</summary>
    Motor,
}

/// <summary>
/// Whether the MVP kinematic solver can carry angular velocity through this part.
///
/// The distinction is deliberately visible everywhere a component appears. A part marked
/// <see cref="UnsupportedBoundary"/> stops propagation and is reported as a boundary, which is
/// the honest outcome, rather than being silently treated as an ordinary gear.
/// </summary>
public enum MechanicalSupport
{
    /// <summary>Ratio propagation through this part is defined and tested.</summary>
    Solved,

    /// <summary>Propagation stops here; the reason is reported rather than guessed past.</summary>
    UnsupportedBoundary,
}

public enum WormHandedness
{
    Right,
    Left,
}

/// <summary>
/// Toothed geometry. <see cref="PitchRadiusLdu"/> is normally left unset and derived from the
/// tooth count, because ordinary Technic gears share one module: two meshing gears sit at
/// (t1 + t2) * 1.25 LDU. That reproduces the known spacings, 8:24 at 40 LDU (two studs) and
/// 8:40 at 60 LDU (three studs). Parts that do not follow it must say so explicitly.
/// </summary>
public sealed record GearGeometry(
    int Teeth,
    float? PitchRadiusLdu = null,
    float? ToothFaceWidthLdu = null)
{
    public const float LduPerTooth = 1.25f;

    public float EffectivePitchRadiusLdu => PitchRadiusLdu ?? (Teeth * LduPerTooth);
}

/// <summary>
/// Worm semantics. The ratio is starts/drivenTeeth. Non-backdrivability is a friction effect
/// and is deliberately absent: a kinematic solver may propagate this constraint in either
/// direction, and claiming otherwise would be predicting load-dependent behaviour.
///
/// <see cref="PitchRadiusLdu"/> cannot be derived from a tooth count the way a gear's can, so
/// it is measured and stated. Without it the centre distance of a worm drive is unpredictable
/// and the mesh can only be accepted on a guessed clearance window.
/// </summary>
public sealed record WormGeometry(
    int Starts,
    WormHandedness Handedness,
    float? PitchRadiusLdu = null);

/// <summary>A powered input and the label the UI offers for it.</summary>
public sealed record MotorOutput(string DefaultInputLabel, string? OutputFeatureId = null);

/// <summary>
/// One part's mechanical semantics.
///
/// <see cref="Axis"/> is normally null, meaning "derive the rotation axis from this part's own
/// keyed axle feature". Hardcoding an axis per part would be wrong: 3647 places its axle hole
/// along Z in part coordinates while other gears use Y, so the geometry is the authority and
/// an explicit value is an override for parts whose feature does not give one.
/// </summary>
public sealed record PartMechanics(
    string Part,
    MechanicalComponentType Type,
    MechanicalSupport Support,
    string Source,
    GearGeometry? Gear = null,
    WormGeometry? Worm = null,
    MotorOutput? Motor = null,
    Vector3? Axis = null,
    ImmutableArray<MechanicalComponentType> MeshesWith = default,
    string? Note = null,
    string? UnsupportedReason = null)
{
    public ImmutableArray<MechanicalComponentType> MeshPartners =>
        MeshesWith.IsDefault ? [] : MeshesWith;

    public bool IsToothed => Gear is not null || Worm is not null;
}

/// <summary>A reviewed part-semantics table, keyed by canonical part name.</summary>
public sealed record MechanicsCatalog(
    ImmutableDictionary<string, PartMechanics> Parts,
    string? SourceDescription = null)
{
    public static MechanicsCatalog Empty { get; } =
        new(ImmutableDictionary<string, PartMechanics>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase));

    public PartMechanics? Find(string canonicalPartName) =>
        Parts.GetValueOrDefault(canonicalPartName);

    public bool IsUnsupportedBoundary(string canonicalPartName) =>
        Find(canonicalPartName)?.Support == MechanicalSupport.UnsupportedBoundary;
}

/// <summary>A catalog entry that failed validation, reported with enough detail to fix the file.</summary>
public sealed record CatalogIssue(string Part, string Reason);

public sealed class CatalogValidationException(IReadOnlyList<CatalogIssue> issues)
    : Exception(BuildMessage(issues))
{
    public IReadOnlyList<CatalogIssue> Issues { get; } = issues;

    private static string BuildMessage(IReadOnlyList<CatalogIssue> issues) =>
        $"The mechanics catalog has {issues.Count} invalid "
        + $"{(issues.Count == 1 ? "entry" : "entries")}:{Environment.NewLine}"
        + string.Join(Environment.NewLine, issues.Select(issue => $"  {issue.Part}: {issue.Reason}"));
}

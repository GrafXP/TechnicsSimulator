using System.Collections.Immutable;
using System.Numerics;
using TechnicsSim.Mechanics.Catalog;

namespace TechnicsSim.Mechanics.Shafts;

/// <summary>
/// A set of logical instances that necessarily share one angular velocity about one axis.
///
/// A shaft is built from keyed connections only. Bearings locate a shaft without joining it,
/// and pin-based structural grouping stays out entirely: over-unioning would silently weld a
/// real mechanism solid, which is far harder to notice than a missing link.
/// </summary>
public sealed record ShaftAssembly(
    string ShaftId,
    ImmutableArray<string> InstanceIds,
    Vector3 Origin,
    Vector3 Axis,
    ImmutableArray<string> MemberFeatureKeys,
    ImmutableArray<string> BearingInstanceIds)
{
    public int MemberCount => InstanceIds.Length;
}

/// <summary>Which shaft a toothed part rides, and the catalog semantics it brings.</summary>
public sealed record MountedGear(
    string InstanceId,
    string CanonicalPartName,
    string ShaftId,
    PartMechanics Mechanics,
    Vector3 Centre,
    Vector3 Axis,
    float PitchRadiusLdu,
    float FaceHalfWidthLdu,
    string AxisSource);

/// <summary>
/// A toothed part the graph deliberately refuses to solve, with the reason preserved.
///
/// These are reported rather than dropped. A clutch that vanishes from the output looks like a
/// bug; a clutch listed as a boundary with its reason is the honest result.
/// </summary>
public sealed record UnsupportedComponent(
    string InstanceId,
    string CanonicalPartName,
    MechanicalComponentType Type,
    string Reason,
    string? ShaftId);

/// <summary>A part whose semantics are needed but absent from the catalog.</summary>
public sealed record UncataloguedComponent(
    string InstanceId,
    string CanonicalPartName,
    string Description,
    int InstanceCount);

/// <summary>
/// A powered input and the shaft it turns.
///
/// Motors are neither gears nor boundaries, so without their own list they would vanish from
/// the graph entirely. The solver needs them as the entry points a user chooses between, and
/// 8275 has four, so more than one driver has to be representable from the start.
/// </summary>
public sealed record MountedDriver(
    string InstanceId,
    string CanonicalPartName,
    string? ShaftId,
    string Label);

public enum GearMeshKind
{
    /// <summary>Parallel axes, external teeth. Reverses direction.</summary>
    ExternalSpur,

    /// <summary>Intersecting perpendicular axes.</summary>
    Bevel,

    /// <summary>Worm driving a wheel at starts/teeth.</summary>
    Worm,
}

/// <summary>
/// One accepted or rejected gear mesh, carrying everything needed to audit it.
///
/// Ratios stay rational. <see cref="RatioNumerator"/> over <see cref="RatioDenominator"/> is the
/// exact tooth ratio; converting to floating point is a display and animation concern, so a
/// three-gear chain composes exactly rather than accumulating rounding.
/// </summary>
public sealed record GearMesh(
    string GearA,
    string GearB,
    string ShaftA,
    string ShaftB,
    GearMeshKind Kind,
    int RatioNumerator,
    int RatioDenominator,
    int Sign,
    float CentreDistanceLdu,
    float ExpectedCentreDistanceLdu,
    float CentreResidualLdu,
    float FaceOverlapLdu,
    float AxisAngleDegrees,
    ConfidenceLevel Confidence,
    string Rule)
{
    /// <summary>Signed angular-velocity ratio omegaB / omegaA, for display only.</summary>
    public double SignedRatio => Sign * (double)RatioNumerator / RatioDenominator;
}

public enum ConfidenceLevel
{
    Low,
    Medium,
    High,
}

/// <summary>Several mesh partners were plausible, so none was chosen.</summary>
public sealed record AmbiguousMesh(
    string GearInstanceId,
    ImmutableArray<string> CandidateInstanceIds,
    string Reason);

public sealed record ShaftGraph(
    ImmutableArray<ShaftAssembly> Shafts,
    ImmutableArray<MountedGear> Gears,
    ImmutableArray<GearMesh> Meshes,
    ImmutableArray<AmbiguousMesh> AmbiguousMeshes,
    ImmutableArray<UnsupportedComponent> UnsupportedComponents,
    ImmutableArray<UncataloguedComponent> UncataloguedComponents,
    ImmutableArray<MountedDriver> Drivers)
{
    public ShaftAssembly? FindShaft(string shaftId) =>
        Shafts.FirstOrDefault(shaft => shaft.ShaftId == shaftId);

    public ShaftAssembly? ShaftForInstance(string instanceId) =>
        Shafts.FirstOrDefault(shaft => shaft.InstanceIds.Contains(instanceId, StringComparer.Ordinal));

    public IEnumerable<GearMesh> MeshesForShaft(string shaftId) =>
        Meshes.Where(mesh => mesh.ShaftA == shaftId || mesh.ShaftB == shaftId);
}

public sealed record ShaftGraphOptions(
    /// <summary>How far a measured centre distance may sit from the module prediction.</summary>
    float CentreDistanceToleranceLdu = 2.0f,

    /// <summary>Parallel-axis tolerance for a spur mesh.</summary>
    float ParallelToleranceDegrees = 5f,

    /// <summary>How near a right angle a bevel or worm mesh must be.</summary>
    float PerpendicularToleranceDegrees = 8f,

    /// <summary>Minimum axial overlap of two tooth faces before a mesh is plausible.</summary>
    float MinimumFaceOverlapLdu = 0.5f,

    /// <summary>Fallback half-width when the catalog gives no tooth-face width.</summary>
    float DefaultFaceHalfWidthLdu = 4f);

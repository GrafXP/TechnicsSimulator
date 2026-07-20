using System.Collections.Immutable;
using System.Numerics;
using TechnicsSim.LDraw.Geometry;

namespace TechnicsSim.Mechanics.Features;

public enum SnapGender
{
    Male,
    Female,
}

public enum CylinderSectionKind
{
    Round,
    Axle,
    Square,
    FlexiblePrevious,
    FlexibleNext,
}

public sealed record CylinderSection(CylinderSectionKind Kind, float Radius, float Length);

public enum SnapCaps
{
    None,
    One,
    Two,
    A,
    B,
}

public enum ScaleInheritancePolicy
{
    None,
    YOnly,
    RadiusOnly,
    YAndRadius,
}

public enum MirrorInheritancePolicy
{
    None,
    Correct,
}

public enum FeatureOrigin
{
    Direct,
    Included,
    Inherited,
}

public abstract record SnapShape;

public sealed record CylinderSnapShape(
    SnapGender Gender,
    ImmutableArray<CylinderSection> Sections,
    SnapCaps Caps,
    bool Centered,
    bool Slide) : SnapShape
{
    public float Length => Sections.Sum(section => section.Length);

    public float MaximumRadius => Sections.IsEmpty ? 0f : Sections.Max(section => section.Radius);
}

public sealed record ClipSnapShape(float Radius, float Length, bool Centered, bool Slide) : SnapShape;

public sealed record FingerSnapShape(
    SnapGender FirstGender,
    ImmutableArray<float> Sequence,
    float Radius,
    bool Centered) : SnapShape
{
    public float Length => Sequence.Sum();
}

public enum GenericBoundsKind
{
    Point,
    Box,
    Cube,
    Cylinder,
    Sphere,
}

public sealed record GenericSnapShape(
    SnapGender Gender,
    GenericBoundsKind BoundsKind,
    Vector3 Extents) : SnapShape;

public sealed record FeatureTransformStep(
    string File,
    int LineNumber,
    string Rule,
    Matrix4x4 Transform);

public sealed record FeatureProvenance(
    string OfficialFile,
    string ShadowFile,
    int ShadowLineNumber,
    string ClassificationRule,
    ImmutableArray<FeatureTransformStep> TransformChain);

/// <summary>An effective, finite snap shape in one canonical part's coordinates.</summary>
public sealed record EffectiveFeature(
    string Key,
    string? Id,
    string? Group,
    Matrix4x4 Transform,
    ScaleInheritancePolicy ScalePolicy,
    MirrorInheritancePolicy MirrorPolicy,
    SnapShape Shape,
    FeatureOrigin Origin,
    FeatureProvenance Provenance);

public sealed record FeatureExtractionIssue(
    string Part,
    string? ShadowFile,
    int LineNumber,
    string Reason);

public sealed record RejectedFeatureInheritance(
    string Part,
    string FeatureKey,
    string DeclaringFile,
    int ReferenceLine,
    string Reason);

public sealed record PartFeatureExtraction(
    string CanonicalPartName,
    ImmutableArray<EffectiveFeature> Features,
    ImmutableArray<FeatureExtractionIssue> Issues,
    ImmutableArray<RejectedFeatureInheritance> RejectedInheritance);

/// <summary>An effective feature placed on one logical model instance.</summary>
public sealed record PlacedFeature(
    string Key,
    string InstanceId,
    string CanonicalPartName,
    EffectiveFeature Feature,
    Matrix4x4 WorldTransform)
{
    public Bounds Bounds => FeatureGeometry.Bounds(this);
}

public readonly record struct FeatureAxis(
    Vector3 Start,
    Vector3 End,
    Vector3 Direction,
    float Radius)
{
    public Vector3 Centre => (Start + End) * 0.5f;

    public float Length => Vector3.Distance(Start, End);
}

public readonly record struct PlacedCylinderSection(
    CylinderSectionKind Kind,
    float Radius,
    Vector3 Start,
    Vector3 End)
{
    public float Length => Vector3.Distance(Start, End);
}

/// <summary>Geometry shared by the matcher, CLI diagnostics, and renderer overlay.</summary>
public static class FeatureGeometry
{
    private const float MinimumScale = 1e-6f;

    public static Vector3 Origin(PlacedFeature feature) =>
        Vector3.Transform(Vector3.Zero, feature.WorldTransform);

    public static FeatureAxis Axis(PlacedFeature feature)
    {
        var shape = feature.Feature.Shape;
        var (length, centered, radius) = shape switch
        {
            CylinderSnapShape cylinder => (cylinder.Length, cylinder.Centered, cylinder.MaximumRadius),
            ClipSnapShape clip => (clip.Length, clip.Centered, clip.Radius),
            FingerSnapShape finger => (finger.Length, finger.Centered, finger.Radius),
            GenericSnapShape generic when generic.BoundsKind == GenericBoundsKind.Cylinder =>
                (generic.Extents.Y * 2f, true, generic.Extents.X),
            _ => (0f, true, 0f),
        };

        var origin = Origin(feature);
        var rawAxis = Vector3.TransformNormal(-Vector3.UnitY, feature.WorldTransform);
        var axialScale = rawAxis.Length();
        var direction = axialScale > MinimumScale ? rawAxis / axialScale : -Vector3.UnitY;
        var worldLength = length * axialScale;
        var start = centered ? origin - (direction * worldLength * 0.5f) : origin;
        var end = start + (direction * worldLength);
        var radiusScale = RadiusScale(feature.WorldTransform);

        return new FeatureAxis(start, end, direction, radius * radiusScale);
    }

    public static ImmutableArray<PlacedCylinderSection> CylinderSections(PlacedFeature feature)
    {
        if (feature.Feature.Shape is not CylinderSnapShape cylinder)
        {
            return [];
        }

        var axis = Axis(feature);
        var axialScale = Vector3.TransformNormal(-Vector3.UnitY, feature.WorldTransform).Length();
        var radialScale = RadiusScale(feature.WorldTransform);
        var cursor = axis.Start;
        var result = ImmutableArray.CreateBuilder<PlacedCylinderSection>(cylinder.Sections.Length);

        foreach (var section in cylinder.Sections)
        {
            var end = cursor + (axis.Direction * section.Length * axialScale);
            result.Add(new PlacedCylinderSection(section.Kind, section.Radius * radialScale, cursor, end));
            cursor = end;
        }

        return result.ToImmutable();
    }

    public static Bounds Bounds(PlacedFeature feature)
    {
        if (feature.Feature.Shape is GenericSnapShape generic)
        {
            return GenericBounds(feature, generic);
        }

        var axis = Axis(feature);
        var radial = new Vector3(axis.Radius);
        return new Bounds(Vector3.Min(axis.Start, axis.End) - radial, Vector3.Max(axis.Start, axis.End) + radial);
    }

    public static float RadiusScale(Matrix4x4 transform)
    {
        var x = Vector3.TransformNormal(Vector3.UnitX, transform).Length();
        var z = Vector3.TransformNormal(Vector3.UnitZ, transform).Length();
        return Math.Max(x, z);
    }

    private static Bounds GenericBounds(PlacedFeature feature, GenericSnapShape generic)
    {
        var origin = Origin(feature);
        if (generic.BoundsKind == GenericBoundsKind.Point)
        {
            return new Bounds(origin, origin);
        }

        var local = generic.BoundsKind switch
        {
            GenericBoundsKind.Box => generic.Extents,
            GenericBoundsKind.Cube => generic.Extents,
            GenericBoundsKind.Sphere => generic.Extents,
            GenericBoundsKind.Cylinder => generic.Extents,
            _ => Vector3.Zero,
        };

        var result = TechnicsSim.LDraw.Geometry.Bounds.Empty;
        for (var i = 0; i < 8; i++)
        {
            var corner = new Vector3(
                (i & 1) == 0 ? -local.X : local.X,
                (i & 2) == 0 ? -local.Y : local.Y,
                (i & 4) == 0 ? -local.Z : local.Z);
            result = result.Include(Vector3.Transform(corner, feature.WorldTransform));
        }

        return result;
    }
}

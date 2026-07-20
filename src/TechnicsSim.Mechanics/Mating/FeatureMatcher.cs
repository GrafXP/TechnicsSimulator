using System.Collections.Immutable;
using System.Numerics;
using TechnicsSim.LDraw.Geometry;
using TechnicsSim.Mechanics.Features;

namespace TechnicsSim.Mechanics.Mating;

/// <summary>AABB sweep broad phase followed by typed, span-aware snap geometry tests.</summary>
public sealed class FeatureMatcher
{
    private readonly MateOptions _options;

    public FeatureMatcher(MateOptions? options = null) => _options = options ?? new MateOptions();

    public ConnectionAnalysis Match(
        IEnumerable<PlacedFeature> source,
        IEnumerable<FeatureExtractionIssue>? extractionIssues = null,
        IEnumerable<RejectedFeatureInheritance>? rejectedInheritance = null)
    {
        var features = source.OrderBy(feature => feature.Key, StringComparer.Ordinal).ToImmutableArray();
        var broadPairs = BroadPhase(features);
        var connections = new List<ConnectionCandidate>();
        var narrowCount = 0;

        foreach (var (a, b) in broadPairs)
        {
            if (a.InstanceId == b.InstanceId || !GroupsCompatible(a.Feature.Group, b.Feature.Group))
            {
                continue;
            }

            narrowCount++;
            if (TryMatch(a, b, out var connection))
            {
                connections.Add(connection!);
            }
        }

        // A logical feature with multiple geometrically valid partners is never resolved by
        // iteration order. Mark every affected edge and surface the complete alternative set.
        var alternatives = connections
            .SelectMany(connection => new[]
            {
                (Feature: connection.FeatureA, Other: connection.FeatureB),
                (Feature: connection.FeatureB, Other: connection.FeatureA),
            })
            .GroupBy(pair => pair.Feature, StringComparer.Ordinal)
            .Select(group => new
            {
                Feature = group.Key,
                Others = group.Select(pair => pair.Other).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray(),
            })
            .Where(group => group.Others.Length > 1)
            .ToArray();
        var ambiguousKeys = alternatives.Select(group => group.Feature).ToHashSet(StringComparer.Ordinal);

        var finalConnections = connections
            .Select(connection => connection with
            {
                IsAmbiguous = ambiguousKeys.Contains(connection.FeatureA) || ambiguousKeys.Contains(connection.FeatureB),
            })
            .OrderBy(connection => connection.InstanceA, StringComparer.Ordinal)
            .ThenBy(connection => connection.InstanceB, StringComparer.Ordinal)
            .ThenBy(connection => connection.FeatureA, StringComparer.Ordinal)
            .ToImmutableArray();

        var matchedKeys = finalConnections
            .SelectMany(connection => new[] { connection.FeatureA, connection.FeatureB })
            .ToHashSet(StringComparer.Ordinal);

        return new ConnectionAnalysis(
            features,
            finalConnections,
            features.Where(feature => !matchedKeys.Contains(feature.Key)).Select(feature => feature.Key).ToImmutableArray(),
            alternatives.Select(group => new AmbiguousCandidate(
                    group.Feature,
                    group.Others,
                    "More than one compatible feature overlaps within the configured tolerances."))
                .ToImmutableArray(),
            extractionIssues?.Distinct().ToImmutableArray() ?? [],
            rejectedInheritance?.Distinct().ToImmutableArray() ?? [],
            broadPairs.Count,
            narrowCount);
    }

    private List<(PlacedFeature A, PlacedFeature B)> BroadPhase(ImmutableArray<PlacedFeature> features)
    {
        var expanded = features
            .Select(feature => (Feature: feature, Bounds: Expand(feature.Bounds, _options.RadialToleranceLdu)))
            .OrderBy(item => item.Bounds.Min.X)
            .ThenBy(item => item.Feature.Key, StringComparer.Ordinal)
            .ToArray();
        var result = new List<(PlacedFeature, PlacedFeature)>();

        for (var i = 0; i < expanded.Length; i++)
        {
            for (var j = i + 1; j < expanded.Length && expanded[j].Bounds.Min.X <= expanded[i].Bounds.Max.X; j++)
            {
                if (Overlaps(expanded[i].Bounds, expanded[j].Bounds))
                {
                    result.Add((expanded[i].Feature, expanded[j].Feature));
                }
            }
        }

        return result;
    }

    private bool TryMatch(PlacedFeature a, PlacedFeature b, out ConnectionCandidate? connection)
    {
        connection = null;
        return (a.Feature.Shape, b.Feature.Shape) switch
        {
            (CylinderSnapShape, CylinderSnapShape) => TryMatchCylinders(a, b, out connection),
            (CylinderSnapShape, ClipSnapShape) => TryMatchCylinderClip(a, b, out connection),
            (ClipSnapShape, CylinderSnapShape) => TryMatchCylinderClip(b, a, out connection),
            (FingerSnapShape, FingerSnapShape) => TryMatchTypedAxes(a, b, "FingerSequenceCandidateRule", out connection),
            (GenericSnapShape, GenericSnapShape) => TryMatchGeneric(a, b, out connection),
            _ => false,
        };
    }

    private bool TryMatchCylinders(PlacedFeature a, PlacedFeature b, out ConnectionCandidate? connection)
    {
        connection = null;
        var shapeA = (CylinderSnapShape)a.Feature.Shape;
        var shapeB = (CylinderSnapShape)b.Feature.Shape;
        if (shapeA.Gender == shapeB.Gender)
        {
            return false;
        }

        var male = shapeA.Gender == SnapGender.Male ? a : b;
        var female = ReferenceEquals(male, a) ? b : a;
        if (!TryAxisOverlap(male, female, out var radial, out var overlap, out var angle))
        {
            return false;
        }

        var maleSections = FeatureGeometry.CylinderSections(male);
        var femaleSections = FeatureGeometry.CylinderSections(female);
        ProfileMatch? best = null;

        foreach (var maleSection in maleSections)
        {
            foreach (var femaleSection in femaleSections)
            {
                var sectionOverlap = ProjectedOverlap(maleSection.Start, maleSection.End, femaleSection.Start, femaleSection.End,
                    FeatureGeometry.Axis(male).Direction);
                if (sectionOverlap < _options.MinimumAxialOverlapLdu)
                {
                    continue;
                }

                if (!TryClassifyProfile(male, female, maleSection, femaleSection, out var profile))
                {
                    continue;
                }

                var clearance = femaleSection.Radius - maleSection.Radius;
                var tolerance = IsFlexible(maleSection.Kind) || IsFlexible(femaleSection.Kind)
                    ? _options.RadialToleranceLdu + 2f
                    : _options.RadialToleranceLdu;
                if (clearance < -_options.RadialToleranceLdu || Math.Abs(clearance) > tolerance)
                {
                    continue;
                }

                var current = profile! with { Overlap = sectionOverlap, Clearance = clearance };
                if (best is null || current.Rank > best.Rank || (current.Rank == best.Rank && current.Overlap > best.Overlap))
                {
                    best = current;
                }
            }
        }

        if (best is null)
        {
            return false;
        }

        connection = Create(
            a, b, best.Kind, best.Confidence, best.Rule,
            radial, Math.Min(overlap, best.Overlap), angle, best.Clearance);
        return true;
    }

    private sealed record ProfileMatch(
        ConnectionKind Kind,
        ConnectionConfidence Confidence,
        string Rule,
        int Rank,
        float Overlap = 0f,
        float Clearance = 0f);

    private static bool TryClassifyProfile(
        PlacedFeature male,
        PlacedFeature female,
        PlacedCylinderSection maleSection,
        PlacedCylinderSection femaleSection,
        out ProfileMatch? match)
    {
        match = null;
        var maleKind = NormalizeFlexible(maleSection.Kind);
        var femaleKind = NormalizeFlexible(femaleSection.Kind);

        if (maleKind == CylinderSectionKind.Axle && femaleKind == CylinderSectionKind.Axle)
        {
            match = new ProfileMatch(
                ConnectionKind.KeyedCoaxial, ConnectionConfidence.High,
                "AxleProfileKeyedRule", 4);
            return true;
        }

        if (maleKind == CylinderSectionKind.Axle && femaleKind == CylinderSectionKind.Round)
        {
            match = new ProfileMatch(
                ConnectionKind.RevoluteBearing, ConnectionConfidence.High,
                "AxleInRoundBoreBearingRule", 3);
            return true;
        }

        if (maleKind == CylinderSectionKind.Round && femaleKind == CylinderSectionKind.Round)
        {
            var stud = ContainsStudId(male.Feature.Id) || ContainsStudId(female.Feature.Id);
            match = stud
                ? new ProfileMatch(
                    ConnectionKind.FixedCandidate, ConnectionConfidence.High,
                    "StudAntiStudSeatingRule", 3)
                : new ProfileMatch(
                    ConnectionKind.GeometricMate, ConnectionConfidence.Medium,
                    "RoundPinHoleAmbiguousMechanicsRule", 2);
            return true;
        }

        if (maleKind == CylinderSectionKind.Square && femaleKind == CylinderSectionKind.Square)
        {
            match = new ProfileMatch(
                ConnectionKind.TypedJointCandidate, ConnectionConfidence.Medium,
                "SquareProfileCandidateRule", 2);
            return true;
        }

        return false;
    }

    private bool TryMatchCylinderClip(
        PlacedFeature cylinder,
        PlacedFeature clip,
        out ConnectionCandidate? connection)
    {
        connection = null;
        if (((CylinderSnapShape)cylinder.Feature.Shape).Gender != SnapGender.Male
            || !TryAxisOverlap(cylinder, clip, out var radial, out var overlap, out var angle))
        {
            return false;
        }

        var cylinderRadius = FeatureGeometry.Axis(cylinder).Radius;
        var clipRadius = FeatureGeometry.Axis(clip).Radius;
        var clearance = clipRadius - cylinderRadius;
        if (Math.Abs(clearance) > _options.RadialToleranceLdu)
        {
            return false;
        }

        connection = Create(
            cylinder, clip,
            ConnectionKind.TypedJointCandidate, ConnectionConfidence.Medium,
            "CylinderClipTypedCandidateRule", radial, overlap, angle, clearance);
        return true;
    }

    private bool TryMatchTypedAxes(
        PlacedFeature a,
        PlacedFeature b,
        string rule,
        out ConnectionCandidate? connection)
    {
        connection = null;
        if (string.IsNullOrEmpty(a.Feature.Group)
            || !TryAxisOverlap(a, b, out var radial, out var overlap, out var angle))
        {
            return false;
        }

        connection = Create(
            a, b,
            ConnectionKind.TypedJointCandidate, ConnectionConfidence.Medium,
            rule, radial, overlap, angle, 0f);
        return true;
    }

    private bool TryMatchGeneric(PlacedFeature a, PlacedFeature b, out ConnectionCandidate? connection)
    {
        connection = null;
        var shapeA = (GenericSnapShape)a.Feature.Shape;
        var shapeB = (GenericSnapShape)b.Feature.Shape;
        if (shapeA.Gender == shapeB.Gender || string.IsNullOrEmpty(a.Feature.Group))
        {
            return false;
        }

        var distance = Vector3.Distance(FeatureGeometry.Origin(a), FeatureGeometry.Origin(b));
        var allowance = GenericRadius(shapeA) + GenericRadius(shapeB) + _options.GenericPositionToleranceLdu;
        if (distance > allowance)
        {
            return false;
        }

        connection = Create(
            a, b,
            ConnectionKind.TypedJointCandidate, ConnectionConfidence.Medium,
            "GenericGroupCandidateRule", distance, 0f, 0f, 0f);
        return true;
    }

    private bool TryAxisOverlap(
        PlacedFeature a,
        PlacedFeature b,
        out float radial,
        out float overlap,
        out float angleDegrees)
    {
        var axisA = FeatureGeometry.Axis(a);
        var axisB = FeatureGeometry.Axis(b);
        var cosine = Math.Clamp(Math.Abs(Vector3.Dot(axisA.Direction, axisB.Direction)), 0f, 1f);
        angleDegrees = MathF.Acos(cosine) * 180f / MathF.PI;
        radial = float.PositiveInfinity;
        overlap = 0f;

        if (angleDegrees > _options.AxisAngleToleranceDegrees)
        {
            return false;
        }

        var delta = axisB.Centre - axisA.Centre;
        radial = (delta - (Vector3.Dot(delta, axisA.Direction) * axisA.Direction)).Length();
        if (radial > _options.RadialToleranceLdu)
        {
            return false;
        }

        overlap = ProjectedOverlap(axisA.Start, axisA.End, axisB.Start, axisB.End, axisA.Direction);
        return overlap >= _options.MinimumAxialOverlapLdu;
    }

    private static float ProjectedOverlap(
        Vector3 aStart,
        Vector3 aEnd,
        Vector3 bStart,
        Vector3 bEnd,
        Vector3 axis)
    {
        var a0 = Vector3.Dot(aStart, axis);
        var a1 = Vector3.Dot(aEnd, axis);
        var b0 = Vector3.Dot(bStart, axis);
        var b1 = Vector3.Dot(bEnd, axis);
        var aMin = Math.Min(a0, a1);
        var aMax = Math.Max(a0, a1);
        var bMin = Math.Min(b0, b1);
        var bMax = Math.Max(b0, b1);
        return Math.Max(0f, Math.Min(aMax, bMax) - Math.Max(aMin, bMin));
    }

    private static ConnectionCandidate Create(
        PlacedFeature a,
        PlacedFeature b,
        ConnectionKind kind,
        ConnectionConfidence confidence,
        string rule,
        float radial,
        float overlap,
        float angle,
        float clearance) =>
        new(
            a.Key, b.Key, a.InstanceId, b.InstanceId,
            kind, confidence, rule,
            new MateResiduals(radial, overlap, angle, clearance),
            false);

    private static bool GroupsCompatible(string? a, string? b) =>
        string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b)
        || !string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.Ordinal);

    private static bool ContainsStudId(string? id) =>
        id?.Contains("stud", StringComparison.OrdinalIgnoreCase) == true;

    private static CylinderSectionKind NormalizeFlexible(CylinderSectionKind kind) => kind switch
    {
        CylinderSectionKind.FlexiblePrevious or CylinderSectionKind.FlexibleNext => CylinderSectionKind.Round,
        _ => kind,
    };

    private static bool IsFlexible(CylinderSectionKind kind) =>
        kind is CylinderSectionKind.FlexiblePrevious or CylinderSectionKind.FlexibleNext;

    private static float GenericRadius(GenericSnapShape shape) => shape.Extents.Length();

    private static Bounds Expand(Bounds bounds, float amount)
    {
        var margin = new Vector3(amount);
        return bounds.IsEmpty ? bounds : new Bounds(bounds.Min - margin, bounds.Max + margin);
    }

    private static bool Overlaps(Bounds a, Bounds b) =>
        !a.IsEmpty && !b.IsEmpty
        && a.Min.X <= b.Max.X && a.Max.X >= b.Min.X
        && a.Min.Y <= b.Max.Y && a.Max.Y >= b.Min.Y
        && a.Min.Z <= b.Max.Z && a.Max.Z >= b.Min.Z;
}

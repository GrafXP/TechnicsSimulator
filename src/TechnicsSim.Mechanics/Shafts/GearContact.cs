using System.Numerics;
using TechnicsSim.Mechanics.Catalog;

namespace TechnicsSim.Mechanics.Shafts;

/// <summary>
/// Decides whether two mounted gears mesh, and at what exact signed ratio.
///
/// The sign is never assumed. It comes from a contact-frame calculation: the tangential
/// velocities of both gears at the shared contact point must agree, so the relative sign is the
/// dot product of the two tangent directions there. That produces the familiar reversal for an
/// external spur pair without hardcoding it, and it stays correct when a shaft axis happens to
/// be stored pointing the other way, or when the axes are perpendicular and "reverses direction"
/// has no fixed meaning.
/// </summary>
public static class GearContact
{
    /// <summary>Marks a quantity this build does not predict, printed as "n/a" rather than 0.</summary>
    public const float NotPredicted = float.NaN;

    public static bool TryMesh(
        MountedGear a, MountedGear b, ShaftGraphOptions options, out GearMesh mesh)
    {
        mesh = null!;

        if (!TypesCanMesh(a.Mechanics, b.Mechanics))
        {
            return false;
        }

        var angle = AxisAngleDegrees(a.Axis, b.Axis);
        var isParallel = angle <= options.ParallelToleranceDegrees
            || angle >= 180f - options.ParallelToleranceDegrees;
        var isPerpendicular = Math.Abs(angle - 90f) <= options.PerpendicularToleranceDegrees;

        var wormSide = WormSide(a, b);
        if (wormSide is not null)
        {
            return isPerpendicular
                && TryWormMesh(wormSide.Value.Worm, wormSide.Value.Wheel, angle, options, out mesh);
        }

        if (isParallel)
        {
            return TrySpurMesh(a, b, angle, options, out mesh);
        }

        if (isPerpendicular)
        {
            return TryBevelMesh(a, b, angle, options, out mesh);
        }

        return false;
    }

    /// <summary>
    /// Parallel axes, external teeth. The prediction is the shared Technic module: two meshing
    /// gears sit at the sum of their pitch radii.
    /// </summary>
    private static bool TrySpurMesh(
        MountedGear a, MountedGear b, float angle, ShaftGraphOptions options, out GearMesh mesh)
    {
        mesh = null!;

        if (a.Mechanics.Gear is null || b.Mechanics.Gear is null)
        {
            return false;
        }

        // A single-sided bevel cannot act as a spur, whatever the axes happen to be doing.
        if (a.Mechanics.Type == MechanicalComponentType.BevelGear
            || b.Mechanics.Type == MechanicalComponentType.BevelGear)
        {
            return false;
        }

        var delta = b.Centre - a.Centre;
        var axialOffset = Vector3.Dot(delta, a.Axis);
        var radial = delta - (a.Axis * axialOffset);
        var centreDistance = radial.Length();
        var expected = a.PitchRadiusLdu + b.PitchRadiusLdu;
        var residual = centreDistance - expected;

        if (Math.Abs(residual) > options.CentreDistanceToleranceLdu)
        {
            return false;
        }

        // Tooth faces must actually line up along the shared axis, or the gears are two
        // separate layers of the same gearbox that merely look adjacent from the side.
        var overlap = a.FaceHalfWidthLdu + b.FaceHalfWidthLdu - Math.Abs(axialOffset);
        if (overlap < options.MinimumFaceOverlapLdu)
        {
            return false;
        }

        var contact = ContactPoint(a, radial);
        var sign = ContactSign(a, b, contact);
        var (numerator, denominator) = Reduce(a.Mechanics.Gear.Teeth, b.Mechanics.Gear.Teeth);

        mesh = new GearMesh(
            GearA: a.InstanceId,
            GearB: b.InstanceId,
            ShaftA: a.ShaftId,
            ShaftB: b.ShaftId,
            Kind: GearMeshKind.ExternalSpur,
            RatioNumerator: numerator,
            RatioDenominator: denominator,
            Sign: sign,
            CentreDistanceLdu: centreDistance,
            ExpectedCentreDistanceLdu: expected,
            CentreResidualLdu: residual,
            FaceOverlapLdu: overlap,
            AxisAngleDegrees: angle,
            Confidence: ConfidenceOf(residual, options),
            Rule: "ExternalSpurModuleRule");

        return true;
    }

    /// <summary>
    /// Perpendicular axes. At least one side must be able to bevel, and the contact point is
    /// taken as the point on A's pitch circle nearest B's axis, which is the same construction
    /// the spur case reduces to.
    /// </summary>
    private static bool TryBevelMesh(
        MountedGear a, MountedGear b, float angle, ShaftGraphOptions options, out GearMesh mesh)
    {
        mesh = null!;

        if (a.Mechanics.Gear is null || b.Mechanics.Gear is null)
        {
            return false;
        }

        if (!CanBevel(a.Mechanics.Type) || !CanBevel(b.Mechanics.Type))
        {
            return false;
        }

        var contact = NearestPointOnPitchCircle(a, b);
        if (contact is null)
        {
            return false;
        }

        var point = contact.Value;

        // The same point must sit at B's pitch radius from B's axis. That single check stands in
        // for the centre-distance test and generalises to intersecting axes.
        var toB = point - b.Centre;
        var radialB = toB - (b.Axis * Vector3.Dot(toB, b.Axis));
        var measured = radialB.Length();
        var residual = measured - b.PitchRadiusLdu;

        if (Math.Abs(residual) > options.CentreDistanceToleranceLdu)
        {
            return false;
        }

        // Both gears must reach the contact point along their own axes.
        var reachA = Math.Abs(Vector3.Dot(point - a.Centre, a.Axis));
        var reachB = Math.Abs(Vector3.Dot(point - b.Centre, b.Axis));
        var slack = Math.Min(
            a.FaceHalfWidthLdu + b.PitchRadiusLdu - reachA,
            b.FaceHalfWidthLdu + a.PitchRadiusLdu - reachB);

        if (slack < options.MinimumFaceOverlapLdu)
        {
            return false;
        }

        var sign = ContactSign(a, b, point);
        var (numerator, denominator) = Reduce(a.Mechanics.Gear.Teeth, b.Mechanics.Gear.Teeth);

        mesh = new GearMesh(
            GearA: a.InstanceId,
            GearB: b.InstanceId,
            ShaftA: a.ShaftId,
            ShaftB: b.ShaftId,
            Kind: GearMeshKind.Bevel,
            RatioNumerator: numerator,
            RatioDenominator: denominator,
            Sign: sign,
            CentreDistanceLdu: measured,
            ExpectedCentreDistanceLdu: b.PitchRadiusLdu,
            CentreResidualLdu: residual,
            FaceOverlapLdu: slack,
            AxisAngleDegrees: angle,
            Confidence: ConfidenceOf(residual, options),
            Rule: "BevelContactFrameRule");

        return true;
    }

    /// <summary>
    /// Worm driving a wheel at starts/teeth.
    ///
    /// The centre distance is predicted from the worm's measured pitch radius, exactly as a gear
    /// pair is, because a worm's radius cannot be derived from a tooth count and so is stated in
    /// the catalog instead. When the catalog gives no radius the prediction is reported as
    /// absent rather than fabricated, and the pair is accepted only on a loose plausibility
    /// window, which is visible in the rule text.
    ///
    /// Confidence is capped at medium regardless of how well the spacing fits. The fit
    /// establishes that the pair meshes and the tooth ratio is exact, but the direction depends
    /// on thread handedness, which the catalog records as the standard right-hand assumption
    /// rather than a verified measurement.
    /// </summary>
    private static bool TryWormMesh(
        MountedGear worm, MountedGear wheel, float angle, ShaftGraphOptions options, out GearMesh mesh)
    {
        mesh = null!;

        if (worm.Mechanics.Worm is null || wheel.Mechanics.Gear is null)
        {
            return false;
        }

        // Perpendicular distance from the wheel's centre to the worm's axis.
        var toWheel = wheel.Centre - worm.Centre;
        var alongWorm = Vector3.Dot(toWheel, worm.Axis);
        var radial = toWheel - (worm.Axis * alongWorm);
        var offset = radial.Length();

        var wormRadius = worm.Mechanics.Worm.PitchRadiusLdu;
        float expected, residual;
        string rule;

        if (wormRadius is { } radius)
        {
            expected = radius + wheel.PitchRadiusLdu;
            residual = offset - expected;
            rule = "WormAxialAdvanceRule (direction assumes right-hand thread)";

            if (Math.Abs(residual) > options.CentreDistanceToleranceLdu)
            {
                return false;
            }
        }
        else
        {
            expected = NotPredicted;
            residual = NotPredicted;
            rule = "WormAxialAdvanceRule (no reviewed worm pitch radius: spacing unvalidated, "
                + "direction assumes right-hand thread)";

            var clearance = offset - wheel.PitchRadiusLdu;
            if (clearance < 0f || clearance > options.CentreDistanceToleranceLdu * 8f)
            {
                return false;
            }
        }

        // The wheel must lie within the worm's own length, not beyond either end of it.
        if (Math.Abs(alongWorm) > worm.FaceHalfWidthLdu + wheel.FaceHalfWidthLdu)
        {
            return false;
        }

        var (numerator, denominator) = Reduce(worm.Mechanics.Worm.Starts, wheel.Mechanics.Gear.Teeth);
        var handedness = worm.Mechanics.Worm.Handedness == WormHandedness.Right ? 1 : -1;

        // A worm drives its wheel by axial thread advance, so the wheel's tangential direction
        // at contact follows the worm axis rather than a rolling contact normal.
        var contact = radial.LengthSquared() > 1e-8f
            ? wheel.Centre - (Vector3.Normalize(radial) * wheel.PitchRadiusLdu)
            : wheel.Centre;
        var wheelTangent = Vector3.Cross(wheel.Axis, contact - wheel.Centre);
        var sign = wheelTangent.LengthSquared() > 1e-8f
            ? Math.Sign(Vector3.Dot(Vector3.Normalize(wheelTangent), worm.Axis)) * handedness
            : handedness;

        mesh = new GearMesh(
            GearA: worm.InstanceId,
            GearB: wheel.InstanceId,
            ShaftA: worm.ShaftId,
            ShaftB: wheel.ShaftId,
            Kind: GearMeshKind.Worm,
            RatioNumerator: numerator,
            RatioDenominator: denominator,
            Sign: sign == 0 ? handedness : sign,
            CentreDistanceLdu: offset,
            ExpectedCentreDistanceLdu: expected,
            CentreResidualLdu: residual,
            FaceOverlapLdu: Math.Abs(alongWorm),
            AxisAngleDegrees: angle,
            Confidence: ConfidenceLevel.Medium,
            Rule: rule);

        return true;
    }

    /// <summary>
    /// The point on A's pitch circle where B's teeth would engage: the one sitting at B's own
    /// pitch radius from B's axis, and, where several do, the one nearest B itself.
    ///
    /// Two details matter. Projecting the direction to B's axis into A's plane looks like the
    /// obvious construction, but it collapses exactly where bevels live: when A's centre sits on
    /// B's axis, that direction is parallel to A's own axis and the projection vanishes.
    /// Sweeping the circle avoids that. The sweep then has its own trap, because a circle
    /// generally has two points equally far from a line, one on each side. Taking the first
    /// found picks the far side half the time and the pair is rejected for being out of reach,
    /// so the tie is broken toward B's centre.
    /// </summary>
    private static Vector3? NearestPointOnPitchCircle(MountedGear a, MountedGear b)
    {
        if (a.PitchRadiusLdu <= 0f)
        {
            return null;
        }

        var (u, v) = PlaneBasis(a.Axis);

        const int coarseSteps = 180;
        const double tieEpsilon = 1e-3;

        var bestAngle = 0.0;
        var bestError = double.MaxValue;
        var bestProximity = double.MaxValue;

        for (var step = 0; step < coarseSteps; step++)
        {
            var angle = 2.0 * Math.PI * step / coarseSteps;
            var error = PitchError(angle);
            var proximity = ProximityToB(angle);

            if (error < bestError - tieEpsilon
                || (error < bestError + tieEpsilon && proximity < bestProximity))
            {
                bestError = Math.Min(error, bestError);
                bestProximity = proximity;
                bestAngle = angle;
            }
        }

        // Refine within the winning arc only, so the tie-break above is never undone.
        var window = 2.0 * Math.PI / coarseSteps;
        var low = bestAngle - window;
        var high = bestAngle + window;

        for (var iteration = 0; iteration < 40; iteration++)
        {
            var third = (high - low) / 3.0;
            var left = low + third;
            var right = high - third;

            if (PitchError(left) < PitchError(right))
            {
                high = right;
            }
            else
            {
                low = left;
            }
        }

        return PointAt((low + high) / 2.0);

        Vector3 PointAt(double angle) => a.Centre
            + (u * (float)(Math.Cos(angle) * a.PitchRadiusLdu))
            + (v * (float)(Math.Sin(angle) * a.PitchRadiusLdu));

        // How far this point is from sitting exactly on B's pitch surface.
        double PitchError(double angle)
        {
            var offset = PointAt(angle) - b.Centre;
            var perpendicular = offset - (b.Axis * Vector3.Dot(offset, b.Axis));
            return Math.Abs(perpendicular.Length() - b.PitchRadiusLdu);
        }

        double ProximityToB(double angle) => (PointAt(angle) - b.Centre).Length();
    }

    /// <summary>Any two orthonormal vectors spanning the plane perpendicular to <paramref name="axis"/>.</summary>
    private static (Vector3 U, Vector3 V) PlaneBasis(Vector3 axis)
    {
        var normalized = Vector3.Normalize(axis);

        // Starting from the least-aligned cardinal keeps the cross product well conditioned.
        var seed = Math.Abs(normalized.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;
        var u = Vector3.Normalize(Vector3.Cross(normalized, seed));
        var v = Vector3.Cross(normalized, u);

        return (u, v);
    }

    private static Vector3 ContactPoint(MountedGear a, Vector3 radialToB) =>
        a.Centre + (Vector3.Normalize(radialToB) * a.PitchRadiusLdu);

    /// <summary>
    /// The relative sign of two meshing gears, from their tangent directions at the contact
    /// point. Rolling contact means the tangential velocities agree there, so the sign of the
    /// ratio is the sign of the dot product of the two tangents.
    /// </summary>
    private static int ContactSign(MountedGear a, MountedGear b, Vector3 contact)
    {
        var tangentA = Vector3.Cross(a.Axis, contact - a.Centre);
        var toB = contact - b.Centre;
        var radialB = toB - (b.Axis * Vector3.Dot(toB, b.Axis));
        var tangentB = Vector3.Cross(b.Axis, radialB);

        if (tangentA.LengthSquared() < 1e-8f || tangentB.LengthSquared() < 1e-8f)
        {
            return -1;
        }

        var dot = Vector3.Dot(Vector3.Normalize(tangentA), Vector3.Normalize(tangentB));
        return dot >= 0f ? 1 : -1;
    }

    private static (MountedGear Worm, MountedGear Wheel)? WormSide(MountedGear a, MountedGear b)
    {
        var aWorm = a.Mechanics.Type == MechanicalComponentType.WormGear;
        var bWorm = b.Mechanics.Type == MechanicalComponentType.WormGear;

        return (aWorm, bWorm) switch
        {
            (true, false) => (a, b),
            (false, true) => (b, a),
            _ => null,
        };
    }

    private static bool TypesCanMesh(PartMechanics a, PartMechanics b) =>
        a.MeshPartners.Contains(b.Type) && b.MeshPartners.Contains(a.Type);

    private static bool CanBevel(MechanicalComponentType type) => type switch
    {
        MechanicalComponentType.BevelGear => true,
        MechanicalComponentType.DoubleBevelGear => true,
        MechanicalComponentType.CrownGear => true,
        _ => false,
    };

    private static ConfidenceLevel ConfidenceOf(float residual, ShaftGraphOptions options)
    {
        var magnitude = Math.Abs(residual);
        if (magnitude <= options.CentreDistanceToleranceLdu * 0.25f)
        {
            return ConfidenceLevel.High;
        }

        return magnitude <= options.CentreDistanceToleranceLdu * 0.6f
            ? ConfidenceLevel.Medium
            : ConfidenceLevel.Low;
    }

    public static float AxisAngleDegrees(Vector3 a, Vector3 b)
    {
        var dot = Math.Clamp(Vector3.Dot(Vector3.Normalize(a), Vector3.Normalize(b)), -1f, 1f);
        return (float)(Math.Acos(dot) * 180.0 / Math.PI);
    }

    /// <summary>Reduces a tooth ratio to lowest terms so chains compose exactly.</summary>
    public static (int Numerator, int Denominator) Reduce(int numerator, int denominator)
    {
        var divisor = Gcd(Math.Abs(numerator), Math.Abs(denominator));
        return divisor == 0 ? (numerator, denominator) : (numerator / divisor, denominator / divisor);
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a;
    }
}

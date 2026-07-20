using System.Numerics;
using TechnicsSim.Mechanics.Catalog;
using TechnicsSim.Mechanics.Shafts;

namespace TechnicsSim.Tests;

/// <summary>
/// Pure-geometry fixtures for gear contact. No library or shadow checkout is needed, so these
/// run everywhere and are the first thing to check when a ratio or direction looks wrong.
/// </summary>
public sealed class GearContactTests
{
    private static readonly ShaftGraphOptions Options = new();

    [Fact]
    public void AnEightToTwentyFourPairMeshesAtTheKnownTwoStudSpacing()
    {
        // The spacing PLAN.md names: 8:24 sits at 40 LDU, two studs.
        var a = Gear("a", teeth: 8, centre: Vector3.Zero, axis: Vector3.UnitY);
        var b = Gear("b", teeth: 24, centre: new Vector3(40, 0, 0), axis: Vector3.UnitY);

        Assert.True(GearContact.TryMesh(a, b, Options, out var mesh));
        Assert.Equal(GearMeshKind.ExternalSpur, mesh.Kind);
        Assert.Equal(40f, mesh.ExpectedCentreDistanceLdu, 3);
        Assert.Equal(0f, mesh.CentreResidualLdu, 3);
        Assert.Equal(ConfidenceLevel.High, mesh.Confidence);
    }

    [Fact]
    public void RatiosStayExactRationalsInLowestTerms()
    {
        var a = Gear("a", teeth: 8, centre: Vector3.Zero, axis: Vector3.UnitY);
        var b = Gear("b", teeth: 24, centre: new Vector3(40, 0, 0), axis: Vector3.UnitY);

        Assert.True(GearContact.TryMesh(a, b, Options, out var mesh));

        // 8/24 reduces to 1/3 rather than being carried as 0.3333.
        Assert.Equal(1, mesh.RatioNumerator);
        Assert.Equal(3, mesh.RatioDenominator);
    }

    [Fact]
    public void AnExternalSpurPairTurnsInOppositeDirections()
    {
        var a = Gear("a", teeth: 8, centre: Vector3.Zero, axis: Vector3.UnitY);
        var b = Gear("b", teeth: 24, centre: new Vector3(40, 0, 0), axis: Vector3.UnitY);

        Assert.True(GearContact.TryMesh(a, b, Options, out var mesh));
        Assert.Equal(-1, mesh.Sign);
    }

    [Fact]
    public void TheSignFollowsTheStoredAxisDirectionRatherThanBeingHardcoded()
    {
        // Same physical pair, but B's axis happens to be stored pointing the other way. Relative
        // to that convention the two gears now agree in sign. Hardcoding -1 for every external
        // spur mesh would get this wrong, which is the trap PLAN.md calls out.
        var a = Gear("a", teeth: 8, centre: Vector3.Zero, axis: Vector3.UnitY);
        var flipped = Gear("b", teeth: 24, centre: new Vector3(40, 0, 0), axis: -Vector3.UnitY);

        Assert.True(GearContact.TryMesh(a, flipped, Options, out var mesh));
        Assert.Equal(1, mesh.Sign);
    }

    [Fact]
    public void GearsTooFarApartDoNotMesh()
    {
        var a = Gear("a", teeth: 8, centre: Vector3.Zero, axis: Vector3.UnitY);
        var b = Gear("b", teeth: 24, centre: new Vector3(48, 0, 0), axis: Vector3.UnitY);

        Assert.False(GearContact.TryMesh(a, b, Options, out _));
    }

    [Fact]
    public void GearsOnParallelAxesInDifferentLayersDoNotMesh()
    {
        // Correct centre distance, but offset along the shared axis by more than the two tooth
        // faces can span. These are two layers of a gearbox that only look adjacent from the side.
        var a = Gear("a", teeth: 8, centre: Vector3.Zero, axis: Vector3.UnitY, halfWidth: 4f);
        var b = Gear("b", teeth: 24, centre: new Vector3(40, 30, 0), axis: Vector3.UnitY, halfWidth: 4f);

        Assert.False(GearContact.TryMesh(a, b, Options, out _));
    }

    [Fact]
    public void PerpendicularDoubleBevelsMeshAndSpursDoNot()
    {
        // 12-tooth double bevel pitch radius is 15 LDU, so two of them meet with their axes
        // crossing at the origin and each centre 15 LDU out along the other's axis.
        var a = Gear("a", teeth: 12, centre: new Vector3(0, -15, 0), axis: Vector3.UnitY,
            type: MechanicalComponentType.DoubleBevelGear);
        var b = Gear("b", teeth: 12, centre: new Vector3(15, 0, 0), axis: Vector3.UnitX,
            type: MechanicalComponentType.DoubleBevelGear);

        Assert.True(GearContact.TryMesh(a, b, Options, out var mesh));
        Assert.Equal(GearMeshKind.Bevel, mesh.Kind);
        Assert.Equal(90f, mesh.AxisAngleDegrees, 1);
        Assert.Equal(1, mesh.RatioNumerator);
        Assert.Equal(1, mesh.RatioDenominator);

        // Plain spur gears have no bevel face, so the same placement must not mesh.
        var spurA = a with { Mechanics = a.Mechanics with { Type = MechanicalComponentType.SpurGear } };
        var spurB = b with { Mechanics = b.Mechanics with { Type = MechanicalComponentType.SpurGear } };
        Assert.False(GearContact.TryMesh(spurA, spurB, Options, out _));
    }

    [Fact]
    public void ASingleSidedBevelNeverActsAsASpur()
    {
        var a = Gear("a", teeth: 20, centre: Vector3.Zero, axis: Vector3.UnitY,
            type: MechanicalComponentType.BevelGear);
        var b = Gear("b", teeth: 20, centre: new Vector3(50, 0, 0), axis: Vector3.UnitY,
            type: MechanicalComponentType.BevelGear);

        // Correct spur spacing for 20:20, but a single-sided bevel cannot mesh on parallel axes.
        Assert.Equal(50f, a.PitchRadiusLdu + b.PitchRadiusLdu, 3);
        Assert.False(GearContact.TryMesh(a, b, Options, out _));
    }

    [Fact]
    public void AThreeGearChainComposesRatioAndSignExactly()
    {
        // 8 -> 24 -> 8 in a line. Each external mesh reverses, so the ends agree, and the exact
        // rationals multiply back to 1:1 with no floating-point drift.
        var a = Gear("a", teeth: 8, centre: Vector3.Zero, axis: Vector3.UnitY);
        var b = Gear("b", teeth: 24, centre: new Vector3(40, 0, 0), axis: Vector3.UnitY);
        var c = Gear("c", teeth: 8, centre: new Vector3(80, 0, 0), axis: Vector3.UnitY);

        Assert.True(GearContact.TryMesh(a, b, Options, out var ab));
        Assert.True(GearContact.TryMesh(b, c, Options, out var bc));

        var numerator = ab.RatioNumerator * bc.RatioNumerator;
        var denominator = ab.RatioDenominator * bc.RatioDenominator;
        var (reducedNumerator, reducedDenominator) = GearContact.Reduce(numerator, denominator);

        Assert.Equal(1, reducedNumerator);
        Assert.Equal(1, reducedDenominator);
        Assert.Equal(1, ab.Sign * bc.Sign);
    }

    [Fact]
    public void AWormReportsStartsOverTeethAndDoesNotFakeACentreDistance()
    {
        // The worm lies along X. Its 24-tooth wheel turns about the perpendicular Z axis, with
        // its centre 34 LDU out: just clear of the wheel's own 30 LDU pitch radius.
        var worm = Gear("worm", teeth: 0, centre: Vector3.Zero, axis: Vector3.UnitX,
            type: MechanicalComponentType.WormGear, halfWidth: 10f);
        var wheel = Gear("wheel", teeth: 24, centre: new Vector3(0, 34, 0), axis: Vector3.UnitZ);

        Assert.Equal(30f, wheel.PitchRadiusLdu, 3);
        Assert.True(GearContact.TryMesh(worm, wheel, Options, out var mesh));
        Assert.Equal(GearMeshKind.Worm, mesh.Kind);

        // One worm start against 24 teeth: one worm turn advances the wheel one tooth.
        Assert.Equal(1, mesh.RatioNumerator);
        Assert.Equal(24, mesh.RatioDenominator);

        // The centre distance is measured but deliberately not predicted, because no reviewed
        // worm pitch radius exists. It must read as absent rather than as a perfect fit.
        Assert.True(float.IsNaN(mesh.ExpectedCentreDistanceLdu));
        Assert.True(float.IsNaN(mesh.CentreResidualLdu));
        Assert.Equal(ConfidenceLevel.Medium, mesh.Confidence);
    }

    private static MountedGear Gear(
        string id,
        int teeth,
        Vector3 centre,
        Vector3 axis,
        MechanicalComponentType type = MechanicalComponentType.SpurGear,
        float halfWidth = 4f)
    {
        var mechanics = new PartMechanics(
            Part: $"{id}.dat",
            Type: type,
            Support: MechanicalSupport.Solved,
            Source: "fixture",
            Gear: type == MechanicalComponentType.WormGear ? null : new GearGeometry(teeth),
            Worm: type == MechanicalComponentType.WormGear ? new WormGeometry(1, WormHandedness.Right) : null,
            MeshesWith:
            [
                MechanicalComponentType.SpurGear,
                MechanicalComponentType.DoubleBevelGear,
                MechanicalComponentType.BevelGear,
                MechanicalComponentType.WormGear,
            ]);

        return new MountedGear(
            InstanceId: id,
            CanonicalPartName: $"{id}.dat",
            ShaftId: $"shaft-{id}",
            Mechanics: mechanics,
            Centre: centre,
            Axis: Vector3.Normalize(axis),
            PitchRadiusLdu: mechanics.Gear?.EffectivePitchRadiusLdu ?? 0f,
            FaceHalfWidthLdu: halfWidth,
            AxisSource: "fixture");
    }
}

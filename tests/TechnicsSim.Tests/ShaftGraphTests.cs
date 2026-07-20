using TechnicsSim.LDraw;
using TechnicsSim.Mechanics.Catalog;
using TechnicsSim.Mechanics.Mating;
using TechnicsSim.Mechanics.Shafts;

namespace TechnicsSim.Tests;

/// <summary>
/// Pins the reconstructed 8275 drivetrain.
///
/// These assertions are about specific hand-checked mechanisms rather than totals, because a
/// count moving is not obviously wrong while the worm drive disappearing is. The 8275 worm pair
/// in particular is the mechanism the MVP animation gate depends on.
/// </summary>
public sealed class ShaftGraphTests
{
    private static ShaftGraph Build(string fileName)
    {
        var model = ModelLoader.Load(
            Path.Combine(TestEnvironment.ModelsDirectory, fileName), [TestEnvironment.Library!]);
        var analysis = ModelConnectionAnalyzer.Analyze(model, TestEnvironment.Shadow!);
        var catalog = CatalogLocator.Load(null, TestEnvironment.RepositoryRoot);

        return ShaftGraphBuilder.Build(analysis, model.Expansion, catalog);
    }

    [ShadowFact]
    public void The8275WormDrivesItsTwentyFourToothWheelAtExactlyOneToTwentyFour()
    {
        var graph = Build("8275-1.mpd");

        var wormMeshes = graph.Meshes.Where(mesh => mesh.Kind == GearMeshKind.Worm).ToList();

        // 8275 has two worms and each drives a 24-tooth wheel.
        Assert.Equal(2, wormMeshes.Count);

        foreach (var mesh in wormMeshes)
        {
            Assert.Equal(1, mesh.RatioNumerator);
            Assert.Equal(24, mesh.RatioDenominator);

            // The measured spacing agrees with the catalog's worm pitch radius. This is the
            // check that would have caught the radius being guessed rather than measured.
            Assert.Equal(40f, mesh.CentreDistanceLdu, 1);
            Assert.Equal(40f, mesh.ExpectedCentreDistanceLdu, 1);
            Assert.Equal(0f, mesh.CentreResidualLdu, 1);

            // Capped at medium: the spacing is validated but the direction rests on the
            // right-hand thread assumption, so it must not claim to be certain.
            Assert.Equal(ConfidenceLevel.Medium, mesh.Confidence);
            Assert.Contains("right-hand thread", mesh.Rule);
        }
    }

    [ShadowFact]
    public void SpurMeshesInTheRealModelSitExactlyOnTheModule()
    {
        var graph = Build("8275-1.mpd");

        var spurs = graph.Meshes.Where(mesh => mesh.Kind == GearMeshKind.ExternalSpur).ToList();

        Assert.NotEmpty(spurs);
        foreach (var mesh in spurs)
        {
            // Real Technic geometry is exact, so anything but a near-zero residual means the
            // module or the axis extraction is wrong rather than that the model is sloppy.
            Assert.True(
                Math.Abs(mesh.CentreResidualLdu) < 0.05f,
                $"{mesh.GearA} <-> {mesh.GearB} sits {mesh.CentreResidualLdu:F3} LDU off the module.");
            Assert.Equal(ConfidenceLevel.High, mesh.Confidence);
        }
    }

    [ShadowFact]
    public void MeshSignsFollowTheStoredAxesRatherThanADefaultReversal()
    {
        var graph = Build("8275-1.mpd");

        var spurs = graph.Meshes.Where(mesh => mesh.Kind == GearMeshKind.ExternalSpur).ToList();

        // 8275 contains spur pairs whose shaft axes are stored both ways round, so a correct
        // contact-frame calculation produces both signs. All-negative would mean the sign was
        // hardcoded and merely looked right.
        Assert.Contains(spurs, mesh => mesh.Sign > 0);
        Assert.Contains(spurs, mesh => mesh.Sign < 0);
    }

    [ShadowFact]
    public void UnsupportedMechanismsAreReportedRatherThanMeshedOrDropped()
    {
        var graph = Build("8275-1.mpd");

        var byType = graph.UnsupportedComponents
            .GroupBy(component => component.Type)
            .ToDictionary(group => group.Key, group => group.Count());

        // Sprockets, clutch gears, and universal joints all exist in 8275 and none of them may
        // be silently treated as an ordinary gear.
        Assert.True(byType.ContainsKey(MechanicalComponentType.Sprocket));
        Assert.True(byType.ContainsKey(MechanicalComponentType.ClutchGear));
        Assert.True(byType.ContainsKey(MechanicalComponentType.UniversalJointEnd));

        Assert.All(graph.UnsupportedComponents, component =>
            Assert.False(string.IsNullOrWhiteSpace(component.Reason)));

        // A boundary part must never also appear as a solved gear.
        var boundaryIds = graph.UnsupportedComponents.Select(c => c.InstanceId).ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain(graph.Gears, gear => boundaryIds.Contains(gear.InstanceId));
    }

    [ShadowFact]
    public void ClutchGearsStayBoundariesEvenThoughTheyHaveToothCounts()
    {
        var graph = Build("8275-1.mpd");

        // A clutch gear has a perfectly good tooth count, which is exactly why it would be easy
        // to mesh by accident. Its coupling to the axle is load-dependent, so it stays out.
        var clutches = graph.UnsupportedComponents
            .Where(component => component.Type == MechanicalComponentType.ClutchGear)
            .ToList();

        Assert.NotEmpty(clutches);
        Assert.All(clutches, clutch => Assert.Contains("sidecar", clutch.Reason));
    }

    [ShadowFact]
    public void ShaftsAreBuiltFromKeyedRelationsAndCarryTheirBearingsSeparately()
    {
        var graph = Build("8275-1.mpd");

        Assert.NotEmpty(graph.Shafts);

        foreach (var shaft in graph.Shafts.Where(shaft => shaft.MemberCount > 1))
        {
            // A bearing supports a shaft without joining it. If an instance were in both, the
            // graph would be transferring torque through a plain bearing.
            Assert.Empty(shaft.InstanceIds.Intersect(shaft.BearingInstanceIds, StringComparer.Ordinal));
            Assert.NotEqual(0f, shaft.Axis.Length());
        }
    }

    [ShadowTheory]
    [InlineData("8275-1.mpd")]
    [InlineData("42055-1.mpd")]
    [InlineData("42100-1.mpd")]
    [InlineData("42121-1.mpd")]
    public void EveryMeshIsAuditable(string fileName)
    {
        var graph = Build(fileName);

        foreach (var mesh in graph.Meshes)
        {
            // The Phase 3 gate: a mesh has to show tooth counts, residuals, confidence, and the
            // rule that produced it, or it cannot be reviewed.
            Assert.False(string.IsNullOrWhiteSpace(mesh.Rule));
            Assert.True(mesh.RatioNumerator > 0 && mesh.RatioDenominator > 0);
            Assert.True(mesh.Sign is 1 or -1);
            Assert.NotEqual(mesh.ShaftA, mesh.ShaftB);

            var a = graph.Gears.Single(gear => gear.InstanceId == mesh.GearA);
            var b = graph.Gears.Single(gear => gear.InstanceId == mesh.GearB);
            Assert.False(string.IsNullOrWhiteSpace(a.AxisSource));
            Assert.False(string.IsNullOrWhiteSpace(b.AxisSource));
        }
    }

    [ShadowTheory]
    [InlineData("8275-1.mpd")]
    [InlineData("42055-1.mpd")]
    [InlineData("42100-1.mpd")]
    [InlineData("42121-1.mpd")]
    public void TheGraphIsDeterministic(string fileName)
    {
        // The Phase 3 gate asks for a deterministic subgraph. Iteration order over dictionaries
        // is the usual way that quietly stops being true.
        var first = Build(fileName);
        var second = Build(fileName);

        Assert.Equal(
            first.Meshes.Select(mesh => $"{mesh.GearA}|{mesh.GearB}|{mesh.Sign}{mesh.RatioNumerator}:{mesh.RatioDenominator}"),
            second.Meshes.Select(mesh => $"{mesh.GearA}|{mesh.GearB}|{mesh.Sign}{mesh.RatioNumerator}:{mesh.RatioDenominator}"));

        Assert.Equal(
            first.Shafts.Select(shaft => $"{shaft.ShaftId}|{string.Join(",", shaft.InstanceIds)}"),
            second.Shafts.Select(shaft => $"{shaft.ShaftId}|{string.Join(",", shaft.InstanceIds)}"));
    }
}

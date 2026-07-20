using TechnicsSim.LDraw.Expansion;
using TechnicsSim.LDraw.Parsing;
using TechnicsSim.LDraw.Resolution;
using TechnicsSim.LDraw.Sources;
using TechnicsSim.Mechanics.Mating;

namespace TechnicsSim.Tests;

public sealed class FeatureMatingTests
{
    private static ConnectionAnalysis Analyze(
        string model,
        string maleShadow,
        string femaleShadow,
        string femalePart = "female.dat")
    {
        var official = new InMemoryFileSource("official")
            .Add("parts/male.dat", "0 Male\n0 !LDRAW_ORG Part")
            .Add($"parts/{femalePart}", "0 Female\n0 !LDRAW_ORG Part");
        var shadow = new InMemoryFileSource("shadow")
            .Add("parts/male.dat", maleShadow)
            .Add($"parts/{femalePart}", femaleShadow);
        var parsed = LDrawParser.Parse(model, "fixture.ldr");
        var resolver = new LDrawResolver(parsed.Documents, [official]);
        var expansion = new LogicalPartExpander(resolver).Expand(parsed.Root);
        return ModelConnectionAnalyzer.Analyze(expansion, resolver, shadow);
    }

    private const string TwoPartModel = """
        0 !LDRAW_ORG Model
        1 16 0 0 0 1 0 0 0 1 0 0 0 1 male.dat
        1 16 0 0 0 1 0 0 0 1 0 0 0 1 female.dat
        """;

    [Fact]
    public void AxleThroughRoundBeamHoleProducesOneBearingAndNoKeyedRelation()
    {
        var analysis = Analyze(
            TwoPartModel,
            "0 !LDCAD SNAP_CYL [gender=M] [caps=none] [secs=A 6 80] [center=true]",
            "0 !LDCAD SNAP_CYL [gender=F] [caps=none] [secs=R 8 2 R 6 16 R 8 2] [center=true]");

        var connection = Assert.Single(analysis.Connections);
        Assert.Equal(ConnectionKind.RevoluteBearing, connection.Kind);
        Assert.Equal(ConnectionConfidence.High, connection.Confidence);
        Assert.Equal("AxleInRoundBoreBearingRule", connection.Rule);
        Assert.DoesNotContain(analysis.Connections, candidate => candidate.Kind == ConnectionKind.KeyedCoaxial);
    }

    [Fact]
    public void AxleThroughGearAxleHoleProducesOneKeyedRelation()
    {
        var analysis = Analyze(
            TwoPartModel,
            "0 !LDCAD SNAP_CYL [gender=M] [caps=none] [secs=A 6 80] [center=true]",
            "0 !LDCAD SNAP_CYL [gender=F] [caps=none] [secs=A 6 20] [center=true]");

        var connection = Assert.Single(analysis.Connections);
        Assert.Equal(ConnectionKind.KeyedCoaxial, connection.Kind);
        Assert.Equal(20f, connection.Residuals.AxialOverlapLdu, 3);
    }

    [Fact]
    public void OriginsMayBeFarApartWhenFiniteSpansOverlap()
    {
        var analysis = Analyze(
            TwoPartModel,
            "0 !LDCAD SNAP_CYL [gender=M] [caps=none] [secs=A 6 80]",
            "0 !LDCAD SNAP_CYL [gender=F] [caps=none] [secs=R 6 20] [pos=0 -60 0]");

        var connection = Assert.Single(analysis.Connections);
        Assert.Equal(ConnectionKind.RevoluteBearing, connection.Kind);
        Assert.Equal(20f, connection.Residuals.AxialOverlapLdu, 3);
    }

    [Fact]
    public void ParallelNeighborOutsideRadialToleranceDoesNotMate()
    {
        var model = """
            0 !LDRAW_ORG Model
            1 16 0 0 0 1 0 0 0 1 0 0 0 1 male.dat
            1 16 2 0 0 1 0 0 0 1 0 0 0 1 female.dat
            """;
        var analysis = Analyze(
            model,
            "0 !LDCAD SNAP_CYL [gender=M] [caps=none] [secs=A 6 80] [center=true]",
            "0 !LDCAD SNAP_CYL [gender=F] [caps=none] [secs=R 6 20] [center=true]");

        Assert.Empty(analysis.Connections);
        Assert.Equal(2, analysis.UnmatchedFeatureKeys.Length);
    }

    [Fact]
    public void FeaturesOnTheSameLogicalPartNeverSelfMate()
    {
        var official = new InMemoryFileSource("official")
            .Add("parts/both.dat", "0 Both\n0 !LDRAW_ORG Part");
        var shadow = new InMemoryFileSource("shadow")
            .Add("parts/both.dat", """
                0 !LDCAD SNAP_CYL [gender=M] [secs=A 6 20] [center=true]
                0 !LDCAD SNAP_CYL [gender=F] [secs=A 6 20] [center=true]
                """);
        var parsed = LDrawParser.Parse(
            "0 !LDRAW_ORG Model\n1 16 0 0 0 1 0 0 0 1 0 0 0 1 both.dat", "fixture.ldr");
        var resolver = new LDrawResolver(parsed.Documents, [official]);
        var expansion = new LogicalPartExpander(resolver).Expand(parsed.Root);

        var analysis = ModelConnectionAnalyzer.Analyze(expansion, resolver, shadow);

        Assert.Empty(analysis.Connections);
    }

    [Fact]
    public void ManyToManyCandidatesAreSurfacedInsteadOfSilentlyChosen()
    {
        var model = """
            0 !LDRAW_ORG Model
            1 16 0 0 0 1 0 0 0 1 0 0 0 1 male.dat
            1 16 0 0 0 1 0 0 0 1 0 0 0 1 female.dat
            1 16 0 0 0 1 0 0 0 1 0 0 0 1 female.dat
            """;
        var analysis = Analyze(
            model,
            "0 !LDCAD SNAP_CYL [gender=M] [secs=A 6 20] [center=true]",
            "0 !LDCAD SNAP_CYL [gender=F] [secs=A 6 20] [center=true]");

        Assert.Equal(2, analysis.Connections.Length);
        Assert.All(analysis.Connections, connection => Assert.True(connection.IsAmbiguous));
        Assert.NotEmpty(analysis.Ambiguities);
    }
}

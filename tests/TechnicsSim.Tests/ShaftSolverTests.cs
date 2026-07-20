using System.Collections.Immutable;
using System.Numerics;
using TechnicsSim.Mechanics.Shafts;
using TechnicsSim.Mechanics.Sidecar;
using TechnicsSim.Mechanics.Solver;

namespace TechnicsSim.Tests;

public sealed class ShaftSolverTests
{
    [Fact]
    public void ThreeGearPathComposesRatioAndSignExactly()
    {
        var graph = Graph(
            Mesh("a", "b", 1, 3, -1),
            Mesh("b", "c", 1, 2, -1));

        var solution = ShaftSolver.Solve(graph, [new ShaftInput("a", ExactRatio.One, "motor")]);

        Assert.True(solution.IsConsistent);
        Assert.Equal(new ExactRatio(-1, 3), solution.Find("b")!.AngularVelocity);
        Assert.Equal(new ExactRatio(1, 6), solution.Find("c")!.AngularVelocity);
        Assert.Equal(2, solution.Find("c")!.Path.Length);
    }

    [Fact]
    public void ConflictingClosedLoopExplainsBothExactAnswers()
    {
        var graph = Graph(
            Mesh("a", "b", 1, 1, -1),
            Mesh("b", "c", 1, 1, -1),
            Mesh("c", "a", 1, 1, -1));

        var solution = ShaftSolver.Solve(graph, [new ShaftInput("a", ExactRatio.One, "motor")]);

        Assert.False(solution.IsConsistent);
        var conflict = Assert.Single(solution.Conflicts);
        Assert.NotEqual(conflict.ExistingVelocity, conflict.ProposedVelocity);
        Assert.Contains("requires", conflict.Message);
        Assert.NotEmpty(conflict.ProposedPath);
    }

    [Fact]
    public void MultipleDriversAreConstraintsAndNeitherSilentlyWins()
    {
        var graph = Graph(Mesh("a", "b", 1, 2, -1));

        var solution = ShaftSolver.Solve(graph,
        [
            new ShaftInput("a", ExactRatio.One, "left motor"),
            new ShaftInput("b", ExactRatio.One, "right motor"),
        ]);

        Assert.False(solution.IsConsistent);
        var conflict = Assert.Single(solution.Conflicts);
        Assert.Contains("left motor", new[] { conflict.ExistingInput, conflict.ProposedInput });
        Assert.Contains("right motor", new[] { conflict.ExistingInput, conflict.ProposedInput });
    }

    [Fact]
    public void DisconnectedShaftsRemainExplicitlyUnsolved()
    {
        var graph = Graph(Mesh("a", "b", 2, 5, 1), extraShafts: ["idle"]);

        var solution = ShaftSolver.Solve(graph, [new ShaftInput("a", ExactRatio.One, "motor")]);

        Assert.Equal(["idle"], solution.UnsolvedShaftIds);
    }

    [ShadowFact]
    public void Reviewed8275MotorReachesAWormWheelAtTheHandCheckedRatio()
    {
        var modelPath = Path.Combine(TestEnvironment.ModelsDirectory, "8275-1.mpd");
        var model = LDraw.ModelLoader.Load(modelPath, [TestEnvironment.Library!]);
        var analysis = Mechanics.Mating.ModelConnectionAnalyzer.Analyze(model, TestEnvironment.Shadow!);
        var catalog = Mechanics.Catalog.CatalogLocator.Load(null, TestEnvironment.RepositoryRoot);
        var sidecar = ModelSidecarIo.LoadFor(modelPath);
        var (graph, _) = SidecarApplication.Build(analysis, model.Expansion, catalog, sidecar);

        // The -X medium motor meshes through the locked 8-tooth/clutch pair, two 16-tooth
        // idlers, and finally the 1-start worm driving a 24-tooth wheel:
        // 1 * 1/3 * 1 * -1 * -1/24 = +1/72.
        var driver = graph.Drivers.Single(candidate =>
            candidate.InstanceId.Contains("m-1n'.ldr@4691", StringComparison.Ordinal));
        var solution = ShaftSolver.Solve(
            graph,
            [new ShaftInput(driver.ShaftId!, ExactRatio.One, driver.Label)]);

        var reachedWormWheels = graph.Meshes
            .Where(mesh => mesh.Kind == GearMeshKind.Worm)
            .Select(mesh => solution.Find(mesh.ShaftB))
            .Where(state => state is not null)
            .ToList();

        var wheel = Assert.Single(reachedWormWheels);
        Assert.Equal(new ExactRatio(1, 72), wheel!.AngularVelocity);
        Assert.True(wheel.Path.Length >= 4);
        Assert.True(solution.IsConsistent);
    }

    [Theory]
    [InlineData(6, -8, -3, 4)]
    [InlineData(-6, -8, 3, 4)]
    [InlineData(0, -8, 0, 1)]
    public void ExactRatiosNormalize(long numerator, long denominator, long expectedNumerator, long expectedDenominator)
    {
        var ratio = new ExactRatio(numerator, denominator);

        Assert.Equal(new BigInteger(expectedNumerator), ratio.Numerator);
        Assert.Equal(new BigInteger(expectedDenominator), ratio.Denominator);
    }

    private static ShaftGraph Graph(GearMesh first, params GearMesh[] remaining) =>
        Graph([first, .. remaining], []);

    private static ShaftGraph Graph(GearMesh first, string[] extraShafts) =>
        Graph([first], extraShafts);

    private static ShaftGraph Graph(IReadOnlyCollection<GearMesh> meshes, IReadOnlyCollection<string> extraShafts)
    {
        var shaftIds = meshes.SelectMany(mesh => new[] { mesh.ShaftA, mesh.ShaftB })
            .Concat(extraShafts)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToImmutableArray();

        return new ShaftGraph(
            [.. shaftIds.Select(id => new ShaftAssembly(id, [id + "-part"], Vector3.Zero, Vector3.UnitY, [], []))],
            [],
            [.. meshes],
            [],
            [],
            [],
            []);
    }

    private static GearMesh Mesh(string a, string b, int numerator, int denominator, int sign) => new(
        $"gear-{a}-{b}-a",
        $"gear-{a}-{b}-b",
        a,
        b,
        GearMeshKind.ExternalSpur,
        numerator,
        denominator,
        sign,
        0,
        0,
        0,
        1,
        0,
        ConfidenceLevel.High,
        "fixture");
}

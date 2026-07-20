using System.Collections.Immutable;
using System.Numerics;
using TechnicsSim.Mechanics.Catalog;
using TechnicsSim.Mechanics.Mating;
using TechnicsSim.Mechanics.Shafts;
using TechnicsSim.Mechanics.Solver;
using TechnicsSim.Wpf.ViewModels;

namespace TechnicsSim.Wpf.Tests;

public sealed class DrivetrainSolutionViewModelTests
{
    [Fact]
    public void ShowsTheSolvedPropagationTreeAndEveryGraphEdge()
    {
        var graph = Graph();
        var solution = ShaftSolver.Solve(
            graph,
            [new ShaftInput("shaft-a", ExactRatio.One, "manual input")]);
        var view = new DrivetrainSolutionViewModel(_ => { });

        view.Load(graph, solution);

        Assert.Contains("3 / 3 shafts solved", view.Summary);
        var root = Assert.Single(view.Roots);
        Assert.Contains("shaft-a", root.Headline);
        Assert.Contains("manual input", root.Detail);
        Assert.Single(root.Children);
        Assert.Single(root.Children[0].Children);
        Assert.Equal(2, view.Constraints.Count);
        Assert.All(view.Constraints, row => Assert.StartsWith("active", row.Status));
        Assert.Empty(view.UnsolvedShafts);
    }

    [Fact]
    public void GraphEdgesCanHighlightTheirTwoGears()
    {
        var highlighted = new List<ImmutableArray<string>>();
        var view = new DrivetrainSolutionViewModel(highlighted.Add);
        view.Load(Graph(), null);

        view.Select(view.Constraints[0]);

        Assert.Equal(["gear-a", "gear-b"], Assert.Single(highlighted));
        Assert.Equal(3, view.UnsolvedShafts.Count);
        Assert.Contains("No driver input", view.Summary);

        view.Select(view.UnsolvedShafts[2]);
        Assert.Equal(["shaft-c-axle"], highlighted[1]);
    }

    [Fact]
    public void ConflictingEdgeAndBothDerivationsAreVisible()
    {
        var graph = Graph();
        graph = graph with
        {
            Meshes =
            [
                .. graph.Meshes,
                Mesh("gear-c", "gear-a", "shaft-c", "shaft-a", 1, 1),
            ],
        };
        var solution = ShaftSolver.Solve(
            graph,
            [new ShaftInput("shaft-a", ExactRatio.One, "manual input")]);
        var view = new DrivetrainSolutionViewModel(_ => { });

        view.Load(graph, solution);

        Assert.Single(view.Conflicts);
        Assert.Contains(view.Constraints, row => row.Status.StartsWith("CONFLICT", StringComparison.Ordinal));
        Assert.Contains("manual input", view.Conflicts[0].ExistingPath + view.Conflicts[0].ProposedPath);
    }

    private static ShaftGraph Graph()
    {
        var shafts = new[] { "shaft-a", "shaft-b", "shaft-c" }
            .Select(id => new ShaftAssembly(
                id,
                [id + "-axle"],
                Vector3.Zero,
                Vector3.UnitY,
                [],
                []))
            .ToImmutableArray();
        var gears = ImmutableArray.Create(
            Gear("gear-a", "shaft-a", "8.dat"),
            Gear("gear-b", "shaft-b", "24.dat"),
            Gear("gear-c", "shaft-c", "16.dat"));
        var meshes = ImmutableArray.Create(
            Mesh("gear-a", "gear-b", "shaft-a", "shaft-b", 1, 3),
            Mesh("gear-b", "gear-c", "shaft-b", "shaft-c", 3, 2));

        return new ShaftGraph(shafts, gears, meshes, [], [], [], []);
    }

    private static MountedGear Gear(string id, string shaft, string part) => new(
        id,
        part,
        shaft,
        new PartMechanics(
            part,
            MechanicalComponentType.SpurGear,
            MechanicalSupport.Solved,
            "fixture",
            Gear: new GearGeometry(8)),
        Vector3.Zero,
        Vector3.UnitY,
        10,
        4,
        "fixture");

    private static GearMesh Mesh(
        string gearA,
        string gearB,
        string shaftA,
        string shaftB,
        int numerator,
        int denominator) => new(
        gearA,
        gearB,
        shaftA,
        shaftB,
        GearMeshKind.ExternalSpur,
        numerator,
        denominator,
        -1,
        40,
        40,
        0,
        4,
        0,
        ConfidenceLevel.High,
        "fixture");
}

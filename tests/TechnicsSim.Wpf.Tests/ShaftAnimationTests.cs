using System.Collections.Immutable;
using System.Numerics;
using TechnicsSim.LDraw.Colours;
using TechnicsSim.LDraw.Geometry;
using TechnicsSim.Mechanics.Catalog;
using TechnicsSim.Mechanics.Shafts;
using TechnicsSim.Mechanics.Solver;
using TechnicsSim.Wpf.Rendering;

namespace TechnicsSim.Wpf.Tests;

public sealed class ShaftAnimationTests
{
    [Fact]
    public void RotatesSolvedMembersAroundTheInitialWorldAxis()
    {
        var initial = Matrix4x4.CreateTranslation(2, 0, 0);
        var scene = Scene(new SceneInstance(
            "gear", "gear.dat", "gear.dat", initial,
            ColourPalette.Fallback.Resolve(4, ColourPalette.Fallback.DefaultContext),
            Bounds.Empty));
        var graph = Graph(new ShaftAssembly("shaft", ["gear"], Vector3.Zero, Vector3.UnitY, [], []));
        var solution = new ShaftSolution(
            [new SolvedShaft("shaft", ExactRatio.One, "motor", [])], [], []);

        var plan = ShaftAnimation.CreatePlan(scene, graph, solution);
        var transforms = ShaftAnimation.BuildTransforms(plan, 0.25);
        var position = Vector3.Transform(Vector3.Zero, transforms["gear"]);

        Assert.Empty(ShaftAnimation.BuildTransforms(plan, 0));
        Assert.Equal(0f, position.X, 4);
        Assert.Equal(0f, position.Y, 4);
        Assert.Equal(-2f, position.Z, 4);
    }

    [Fact]
    public void UnsupportedPartsAndMotorHousingsRemainStatic()
    {
        var scene = Scene(
            Instance("motor"),
            Instance("boundary"),
            Instance("axle"));
        var graph = new ShaftGraph(
            [new ShaftAssembly("shaft", ["motor", "boundary", "axle"], Vector3.Zero, Vector3.UnitY, [], [])],
            [], [], [],
            [new UnsupportedComponent("boundary", "3712.dat", MechanicalComponentType.UniversalJointEnd, "deferred", "shaft")],
            [],
            [new MountedDriver("motor", "58120.dat", "shaft", "motor")]);
        var solution = new ShaftSolution(
            [new SolvedShaft("shaft", ExactRatio.One, "motor", [])], [], []);

        var plan = ShaftAnimation.CreatePlan(scene, graph, solution);
        var transforms = ShaftAnimation.BuildTransforms(plan, 0.5);

        Assert.DoesNotContain("motor", transforms.Keys);
        Assert.DoesNotContain("boundary", transforms.Keys);
        Assert.Contains("axle", transforms.Keys);
    }

    private static SceneInstance Instance(string id) => new(
        id, id + ".dat", id + ".dat", Matrix4x4.Identity,
        ColourPalette.Fallback.Resolve(4, ColourPalette.Fallback.DefaultContext),
        Bounds.Empty);

    private static RenderScene Scene(params SceneInstance[] instances) => new(
        [.. instances], [],
        ImmutableDictionary<string, PartMesh>.Empty,
        new PartMesh("static", [], [], Bounds.Empty, 0, 0, []),
        Bounds.Empty,
        []);

    private static ShaftGraph Graph(params ShaftAssembly[] shafts) => new(
        [.. shafts], [], [], [], [], [], []);
}

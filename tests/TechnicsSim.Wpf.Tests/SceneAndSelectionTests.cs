using System.IO;
using System.Windows;
using TechnicsSim.LDraw.Colours;
using TechnicsSim.LDraw.Expansion;
using TechnicsSim.LDraw.Geometry;
using TechnicsSim.LDraw.Parsing;
using TechnicsSim.LDraw.Resolution;
using TechnicsSim.LDraw.Sources;
using TechnicsSim.Wpf.Rendering;
using TechnicsSim.Wpf.ViewModels;

namespace TechnicsSim.Wpf.Tests;

/// <summary>
/// A renderer stand-in. It records what it was asked to draw and lets a test dictate what a
/// click resolves to, so scene ownership and selection can be asserted without a GPU.
/// </summary>
internal sealed class FakeRenderer : ISceneRenderer
{
    public RenderScene? Loaded { get; private set; }

    public string? Highlighted { get; private set; }

    public string? NextPick { get; set; }

    public int ClearCount { get; private set; }

    public bool ShowEdges { get; set; }

    public bool ShowDiagnostics { get; set; }

    public RenderStatistics Load(RenderScene scene)
    {
        Loaded = scene;
        return new RenderStatistics(
            scene.Instances.Length, scene.Batches.Length, scene.DistinctMeshGroups,
            scene.UploadedTriangleCount, scene.TriangleCount, 0);
    }

    public void Clear()
    {
        ClearCount++;
        Loaded = null;
    }

    public string? PickInstance(Point viewportPoint) => NextPick;

    public void Highlight(string? instanceId) => Highlighted = instanceId;

    public void ZoomToFit()
    {
    }

    public void ZoomToInstance(string instanceId)
    {
    }
}

public sealed class SceneAndSelectionTests
{
    /// <summary>A two-triangle part so every instance has real geometry and bounds.</summary>
    private const string PartText = """
        0 Test Part
        0 !LDRAW_ORG Part
        0 BFC CERTIFY CCW
        3 16 0 0 0  0 0 10  10 0 0
        """;

    private static RenderScene BuildScene(string mpdText)
    {
        var source = new InMemoryFileSource("stub")
            .Add("parts/brick.dat", PartText)
            .Add("parts/other.dat", PartText);

        var parsed = LDrawParser.Parse(mpdText, "test.mpd");
        var resolver = new LDrawResolver(parsed.Documents, [source]);
        var expansion = new LogicalPartExpander(resolver).Expand(parsed.Root);
        var cache = new PartMeshCache(resolver, "test-revision");

        return new SceneBuilder(cache, ColourPalette.Fallback).Build(expansion);
    }

    [Fact]
    public void GroupsRepeatedPartsIntoOneBatchPerColour()
    {
        // The instancing claim in miniature: many placements, one vertex buffer.
        var scene = BuildScene("""
            0 FILE main.ldr
            0 !LDRAW_ORG Model
            1 4 0 0 0 1 0 0 0 1 0 0 0 1 brick.dat
            1 4 20 0 0 1 0 0 0 1 0 0 0 1 brick.dat
            1 4 40 0 0 1 0 0 0 1 0 0 0 1 brick.dat
            """);

        Assert.Equal(3, scene.Instances.Length);
        var batch = Assert.Single(scene.Batches);
        Assert.Equal(3, batch.InstanceCount);
        Assert.Equal(1, scene.DistinctMeshGroups);
    }

    [Fact]
    public void SplitsBatchesByColourWhileSharingOneVertexBuffer()
    {
        var scene = BuildScene("""
            0 FILE main.ldr
            0 !LDRAW_ORG Model
            1 4 0 0 0 1 0 0 0 1 0 0 0 1 brick.dat
            1 0 20 0 0 1 0 0 0 1 0 0 0 1 brick.dat
            """);

        // Two materials, but colour lives in the material rather than the vertices, so the
        // geometry is uploaded once.
        Assert.Equal(2, scene.Batches.Length);
        Assert.Equal(1, scene.DistinctMeshGroups);
        Assert.Equal(2, scene.Batches.Select(b => b.Colour.Code).Distinct().Count());
    }

    [Fact]
    public void CountsDrawnTrianglesSeparatelyFromUploadedOnes()
    {
        var scene = BuildScene("""
            0 FILE main.ldr
            0 !LDRAW_ORG Model
            1 4 0 0 0 1 0 0 0 1 0 0 0 1 brick.dat
            1 4 20 0 0 1 0 0 0 1 0 0 0 1 brick.dat
            1 4 40 0 0 1 0 0 0 1 0 0 0 1 brick.dat
            1 4 60 0 0 1 0 0 0 1 0 0 0 1 brick.dat
            """);

        Assert.Equal(1, scene.UploadedTriangleCount);
        Assert.Equal(4, scene.TriangleCount);
    }

    [Fact]
    public void EveryInstanceKeepsItsOwnIdAndWorldBounds()
    {
        var scene = BuildScene("""
            0 FILE main.ldr
            0 !LDRAW_ORG Model
            1 4 0 0 0 1 0 0 0 1 0 0 0 1 brick.dat
            1 4 100 0 0 1 0 0 0 1 0 0 0 1 brick.dat
            """);

        Assert.Equal(
            scene.Instances.Length,
            scene.Instances.Select(i => i.InstanceId).Distinct().Count());

        Assert.Equal(0f, scene.Instances[0].WorldBounds.Min.X, 3);
        Assert.Equal(100f, scene.Instances[1].WorldBounds.Min.X, 3);
    }

    [Fact]
    public void BatchIndicesPointAtTheRightInstances()
    {
        // This is the link the renderer relies on to build its identity map, so a mismatch
        // here would mis-select in the viewport.
        var scene = BuildScene("""
            0 FILE main.ldr
            0 !LDRAW_ORG Model
            1 4 0 0 0 1 0 0 0 1 0 0 0 1 brick.dat
            1 0 20 0 0 1 0 0 0 1 0 0 0 1 other.dat
            1 4 40 0 0 1 0 0 0 1 0 0 0 1 brick.dat
            """);

        foreach (var batch in scene.Batches)
        {
            foreach (var index in batch.InstanceIndices)
            {
                Assert.Equal(batch.CanonicalPartName, scene.Instances[index].CanonicalPartName);
            }
        }
    }

    [Fact]
    public void SeparatesStaticInlineGeometryFromSelectableInstances()
    {
        // Inline model geometry stands in for the generated hose and spring meshes: drawn,
        // but never a logical part and never selectable.
        var scene = BuildScene("""
            0 FILE main.ldr
            0 !LDRAW_ORG Model
            1 4 0 0 0 1 0 0 0 1 0 0 0 1 brick.dat
            3 4 0 0 0  0 0 50  50 0 0
            """);

        Assert.Single(scene.Instances);
        Assert.Equal(1, scene.StaticGeometry.TriangleCount);
    }

    [Fact]
    public void BuildsATreeMirroringTheSubmodelHierarchy()
    {
        var scene = BuildScene("""
            0 FILE main.ldr
            0 !LDRAW_ORG Model
            1 16 0 0 0 1 0 0 0 1 0 0 0 1 sub.ldr
            1 16 100 0 0 1 0 0 0 1 0 0 0 1 sub.ldr

            0 FILE sub.ldr
            0 !LDRAW_ORG Model
            1 4 0 0 0 1 0 0 0 1 0 0 0 1 brick.dat
            """);

        var root = ModelTreeNode.Build(scene, "main.ldr");

        // Two distinct references to the same submodel are two branches, not one shared node.
        Assert.Equal(2, root.Children.Count);
        Assert.All(root.Children, c => Assert.Equal("sub.ldr", c.Label));
        Assert.All(root.Children, c => Assert.Single(c.Children));
    }

    [Fact]
    public void EveryTreeLeafCarriesASelectableInstanceId()
    {
        var scene = BuildScene("""
            0 FILE main.ldr
            0 !LDRAW_ORG Model
            1 4 0 0 0 1 0 0 0 1 0 0 0 1 brick.dat
            1 0 20 0 0 1 0 0 0 1 0 0 0 1 other.dat
            """);

        var root = ModelTreeNode.Build(scene, "main.ldr");
        var leafIds = Leaves(root).Select(l => l.InstanceId).OfType<string>().Order().ToList();

        Assert.Equal(scene.Instances.Select(i => i.InstanceId).Order().ToList(), leafIds);
    }

    private static IEnumerable<ModelTreeNode> Leaves(ModelTreeNode node) =>
        node.Children.Count == 0 ? [node] : node.Children.SelectMany(Leaves);

    [Fact]
    public void AViewportClickSelectsAndHighlightsTheSameInstance()
    {
        var renderer = new FakeRenderer();
        var viewModel = new MainViewModel(renderer, TestPaths.RepositoryRoot);

        renderer.NextPick = "main.ldr@3";
        viewModel.SelectAt(new Point(10, 10));

        Assert.Equal("main.ldr@3", viewModel.SelectedInstanceId);
        Assert.Equal("main.ldr@3", renderer.Highlighted);
    }

    [Fact]
    public void ClickingEmptySpaceClearsTheSelection()
    {
        var renderer = new FakeRenderer();
        var viewModel = new MainViewModel(renderer, TestPaths.RepositoryRoot);

        renderer.NextPick = "main.ldr@3";
        viewModel.SelectAt(new Point(10, 10));

        renderer.NextPick = null;
        viewModel.SelectAt(new Point(500, 500));

        Assert.Null(viewModel.SelectedInstanceId);
        Assert.Null(renderer.Highlighted);
    }

    [Fact]
    public void SelectingInTheTreeHighlightsInTheViewport()
    {
        var renderer = new FakeRenderer();
        var viewModel = new MainViewModel(renderer, TestPaths.RepositoryRoot)
        {
            SelectedNode = new ModelTreeNode("brick.dat", "main.ldr@7"),
        };

        Assert.Equal("main.ldr@7", viewModel.SelectedInstanceId);
        Assert.Equal("main.ldr@7", renderer.Highlighted);
    }

    [Fact]
    public void SelectingAGroupNodeDoesNotChangeTheSelection()
    {
        var renderer = new FakeRenderer();
        var viewModel = new MainViewModel(renderer, TestPaths.RepositoryRoot);

        renderer.NextPick = "main.ldr@3";
        viewModel.SelectAt(new Point(10, 10));

        // A submodel group carries no instance ID, so it must not clear a real selection.
        viewModel.SelectedNode = new ModelTreeNode("sub.ldr");

        Assert.Equal("main.ldr@3", viewModel.SelectedInstanceId);
    }
}

internal static class TestPaths
{
    public static string RepositoryRoot { get; } = Find();

    private static string Find()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git"))
                || File.Exists(Path.Combine(directory.FullName, "TechnicsSimulator.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return Directory.GetCurrentDirectory();
    }
}

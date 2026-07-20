using System.IO;
using System.Numerics;
using System.Windows;
using TechnicsSim.LDraw.Colours;
using TechnicsSim.LDraw.Expansion;
using TechnicsSim.LDraw.Geometry;
using TechnicsSim.LDraw.Library;
using TechnicsSim.LDraw.Parsing;
using TechnicsSim.LDraw.Resolution;
using TechnicsSim.LDraw.Sources;
using TechnicsSim.Mechanics.Mating;
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

    public IReadOnlyCollection<string> HighlightedSet { get; private set; } = [];

    /// <summary>The primary highlighted instance, which is all most tests care about.</summary>
    public string? Highlighted => HighlightedSet.FirstOrDefault();

    public string? NextPick { get; set; }

    public int ClearCount { get; private set; }

    public bool ShowEdges { get; set; }

    public bool ShowDiagnostics { get; set; }

    public bool EmphasizeMechanics { get; set; }

    public double GhostOpacity { get; set; } = 0.2;

    public IReadOnlyCollection<string> MechanicalInstances { get; private set; } = [];

    public ConnectionAnalysis? Diagnostics { get; private set; }

    public IReadOnlyCollection<string> ZoomedInstances { get; private set; } = [];

    public IReadOnlyDictionary<string, Matrix4x4> InstanceTransforms { get; private set; } =
        new Dictionary<string, Matrix4x4>();

    public RenderStatistics Load(RenderScene scene)
    {
        Loaded = scene;
        return new RenderStatistics(
            scene.Instances.Length, scene.Batches.Length, scene.DistinctMeshGroups,
            scene.UploadedTriangleCount, scene.TriangleCount, 0);
    }

    public void SetMechanicsDiagnostics(ConnectionAnalysis? analysis) => Diagnostics = analysis;

    public void Clear()
    {
        ClearCount++;
        Loaded = null;
    }

    public string? PickInstance(Point viewportPoint) => NextPick;

    public void Highlight(IReadOnlyCollection<string> instanceIds) => HighlightedSet = instanceIds;

    public void SetMechanicalInstances(IEnumerable<string> instanceIds) =>
        MechanicalInstances = instanceIds.ToList();

    public void SetInstanceTransforms(IReadOnlyDictionary<string, Matrix4x4> transforms) =>
        InstanceTransforms = new Dictionary<string, Matrix4x4>(transforms, StringComparer.Ordinal);

    public void ZoomToFit()
    {
    }

    public void ZoomToInstances(IReadOnlyCollection<string> instanceIds) =>
        ZoomedInstances = instanceIds;
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
    public void ZoomToSelectionFramesTheWholeSelectedSet()
    {
        var renderer = new FakeRenderer();
        var viewModel = new MainViewModel(renderer, TestPaths.RepositoryRoot)
        {
            SelectedNode = new ModelTreeNode("brick.dat", "main.ldr@7"),
        };

        viewModel.ZoomToSelection();

        Assert.Equal(["main.ldr@7"], renderer.ZoomedInstances);
    }

    [Fact]
    public void IsolationStaysOffUntilThereIsADrivetrainToIsolate()
    {
        var renderer = new FakeRenderer();
        var viewModel = new MainViewModel(renderer, TestPaths.RepositoryRoot);

        // Without a catalog or shadow library there is no graph, so the checkbox has nothing to
        // act on and fading the whole model would just hide it.
        Assert.False(viewModel.HasMechanicalInstances);
        Assert.False(viewModel.EmphasizeMechanics);
    }

    [Fact]
    public void TheContextOpacityDefaultsToMostlyTransparent()
    {
        var viewModel = new MainViewModel(new FakeRenderer(), TestPaths.RepositoryRoot);

        Assert.Equal(0.2, viewModel.GhostOpacity, 3);
    }

    [Fact]
    public async Task Reviewed8275GraphFeedsTheViewerAnimationEndToEndWhenLibrariesAreAvailable()
    {
        using var libraryProbe = LibraryLocator.Locate(null, TestPaths.RepositoryRoot)?.Source;
        if (libraryProbe is null || LibraryLocator.LocateShadow(null, TestPaths.RepositoryRoot) is null)
        {
            // The viewer remains usable without optional external data; CI jobs that do not
            // bootstrap the libraries cannot exercise this real-model integration path.
            return;
        }

        var renderer = new FakeRenderer();
        var viewModel = new MainViewModel(renderer, TestPaths.RepositoryRoot);
        viewModel.Initialize();

        await viewModel.LoadModelAsync(Path.Combine(TestPaths.RepositoryRoot, "Models", "8275-1.mpd"));

        Assert.True(viewModel.CanAnimate);
        Assert.Contains("15 of 109 shafts solved", viewModel.AnimationStatus);
        Assert.Equal("All reviewed drivers (4)", viewModel.SelectedAnimationInput?.DisplayName);

        viewModel.AnimationTurns = 0.25;
        Assert.NotEmpty(renderer.InstanceTransforms);

        renderer.NextPick = renderer.InstanceTransforms.Keys.First();
        viewModel.SelectAt(new Point(10, 10));
        Assert.Contains("omega", viewModel.SelectionDetail);

        viewModel.ToggleAnimation();
        viewModel.AdvanceAnimation(0.5);
        Assert.True(viewModel.AnimationTurns > 0.25);
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

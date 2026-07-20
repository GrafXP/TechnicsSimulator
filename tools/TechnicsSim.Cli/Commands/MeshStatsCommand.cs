using System.Diagnostics;
using TechnicsSim.LDraw;
using TechnicsSim.LDraw.Colours;
using TechnicsSim.LDraw.Geometry;

namespace TechnicsSim.Cli.Commands;

/// <summary>
/// Builds a model's full render scene without a window and reports what the renderer would be
/// asked to do.
///
/// This exists so the Phase 1 instancing and geometry claims can be checked headlessly and in
/// CI. "Track links are instanced rather than expanded into independent vertex buffers" is a
/// measurable statement about uploaded versus drawn triangles, not something to eyeball.
/// </summary>
public static class MeshStatsCommand
{
    public static int Run(Workspace workspace, CommandLine commandLine)
    {
        var modelPath = commandLine.RequirePositional(0, "<model.mpd>");
        var library = workspace.RequireLibrary();

        var loadWatch = Stopwatch.StartNew();
        var model = ModelLoader.Load(modelPath, [library], commandLine.Option("root"));
        loadWatch.Stop();

        var palette = ColourPalette.Load(library);
        var revision = workspace.LibraryInfo?.Sha256
            ?? workspace.LibraryInfo?.UpdateTag
            ?? "unknown";

        var cache = new PartMeshCache(model.Resolver, revision, new MeshBuildOptions(
            IncludeEdges: !commandLine.Flag("no-edges")));

        var buildWatch = Stopwatch.StartNew();
        var scene = new SceneBuilder(cache, palette).Build(model.Expansion);
        buildWatch.Stop();

        Console.WriteLine($"Model   : {Path.GetFileName(model.Path)}  (root '{model.Root.Name}')");
        Console.WriteLine($"Palette : {palette.Count:N0} colours");
        Console.WriteLine();

        Console.WriteLine($"  Logical instances          : {scene.Instances.Length,10:N0}");
        Console.WriteLine($"  Distinct parts             : {scene.Meshes.Count,10:N0}");
        Console.WriteLine($"  Instanced batches          : {scene.Batches.Length,10:N0}");
        Console.WriteLine($"  Distinct vertex buffers    : {scene.DistinctMeshGroups,10:N0}");
        Console.WriteLine();

        var drawn = scene.TriangleCount;
        var uploaded = scene.UploadedTriangleCount;
        Console.WriteLine($"  Triangles uploaded         : {uploaded,10:N0}");
        Console.WriteLine($"  Triangles drawn            : {drawn,10:N0}");
        Console.WriteLine($"  Instancing saving          : {Ratio(drawn, uploaded),10}");
        Console.WriteLine();

        var inline = scene.StaticGeometry;
        Console.WriteLine(
            $"  Static inline geometry     : {inline.TriangleCount,10:N0} tris, "
            + $"{inline.EdgeSegmentCount:N0} edges  (hose/spring fallback, not selectable)");
        Console.WriteLine();

        Console.WriteLine($"  Parse + expand             : {loadWatch.ElapsedMilliseconds,10:N0} ms");
        Console.WriteLine($"  Mesh build + batch         : {buildWatch.ElapsedMilliseconds,10:N0} ms");
        Console.WriteLine($"  Mesh cache hits / misses   : {cache.Hits,10:N0} / {cache.Misses:N0}");
        Console.WriteLine();

        var bounds = scene.Bounds;
        Console.WriteLine($"  Model bounds (LDU)         : {Format(bounds.Min)} .. {Format(bounds.Max)}");
        Console.WriteLine($"  Model size (LDU)           : {Format(bounds.Size)}");
        Console.WriteLine();

        Console.WriteLine("  Largest batches by instance count:");
        foreach (var batch in scene.Batches
            .OrderByDescending(b => b.InstanceCount)
            .ThenBy(b => b.CanonicalPartName, StringComparer.Ordinal)
            .Take(10))
        {
            var group = scene.Meshes[batch.CanonicalPartName].Groups[batch.MeshGroupIndex];
            Console.WriteLine(
                $"    {batch.InstanceCount,6:N0} x {group.TriangleCount,6:N0} tris  "
                + $"{batch.CanonicalPartName} [group {batch.MeshGroupIndex}, colour {batch.Colour.Code}]"
                + (batch.DoubleSided ? " (double-sided)" : string.Empty));
        }

        var doubleSided = scene.Batches.Where(b => b.DoubleSided).ToList();
        if (doubleSided.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine(
                $"  Double-sided batches       : {doubleSided.Count:N0}"
                + "  (uncertified geometry, rendered without culling rather than assumed certified)");
        }

        if (scene.PartsWithoutGeometry.Length > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"  Parts with no geometry     : {scene.PartsWithoutGeometry.Length:N0}");
            foreach (var part in scene.PartsWithoutGeometry.Take(10))
            {
                Console.WriteLine($"    {part}");
            }
        }

        var unresolved = scene.Meshes.Values
            .SelectMany(m => m.UnresolvedReferences)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        if (unresolved.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"  Unresolved geometry refs   : {unresolved.Count:N0}");
            foreach (var reference in unresolved.Take(10))
            {
                Console.WriteLine($"    {reference}");
            }
        }

        return unresolved.Count == 0 ? 0 : 2;
    }

    private static string Ratio(int drawn, int uploaded) =>
        uploaded == 0 ? "n/a" : $"{(double)drawn / uploaded:N1}x";

    private static string Format(System.Numerics.Vector3 v) =>
        $"({v.X,8:N1},{v.Y,8:N1},{v.Z,8:N1})";
}

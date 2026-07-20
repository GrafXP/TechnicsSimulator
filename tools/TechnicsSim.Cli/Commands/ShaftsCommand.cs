using TechnicsSim.LDraw;
using TechnicsSim.Mechanics.Catalog;
using TechnicsSim.Mechanics.Mating;
using TechnicsSim.Mechanics.Shafts;

namespace TechnicsSim.Cli.Commands;

/// <summary>
/// Reports shaft assemblies, gear meshes, and the boundaries the graph refuses to cross.
///
/// The unsupported and uncatalogued sections are as much the point as the meshes. A drivetrain
/// report that lists only what it solved cannot be reviewed, because the reader cannot tell a
/// mechanism that was handled from one that was quietly skipped.
/// </summary>
public static class ShaftsCommand
{
    public static int Run(Workspace workspace, CommandLine commandLine)
    {
        var modelPath = commandLine.RequirePositional(0, "<model.mpd>");
        var model = ModelLoader.Load(modelPath, [workspace.RequireLibrary()], commandLine.Option("root"));
        var analysis = ModelConnectionAnalyzer.Analyze(model, workspace.RequireShadow());

        var catalog = CatalogLocator.Load(commandLine.Option("catalog"));
        var graph = ShaftGraphBuilder.Build(analysis, model.Expansion, catalog);

        var top = ParseTop(commandLine.Option("top"));

        Console.WriteLine($"Model  : {Path.GetFileName(modelPath)}  (root '{model.Root.Name}')");
        Console.WriteLine($"Library: {workspace.LibraryInfo?.UpdateTag ?? "(unknown)"}"
            + $"  |  Shadow: {Short(workspace.ShadowInfo?.GitCommit)}");
        Console.WriteLine($"Catalog: {catalog.Parts.Count} parts");
        Console.WriteLine();

        var solvedShafts = graph.Shafts.Count(shaft => shaft.MemberCount > 1);
        Console.WriteLine($"  Shaft assemblies          : {graph.Shafts.Length,8:N0}  ({solvedShafts:N0} with more than one member)");
        Console.WriteLine($"  Mounted gears             : {graph.Gears.Length,8:N0}");
        Console.WriteLine($"  Gear meshes               : {graph.Meshes.Length,8:N0}");
        Console.WriteLine($"  Ambiguous meshes          : {graph.AmbiguousMeshes.Length,8:N0}");
        Console.WriteLine($"  Unsupported components    : {graph.UnsupportedComponents.Length,8:N0}");
        Console.WriteLine($"  Uncatalogued components   : {graph.UncataloguedComponents.Length,8:N0}");

        WriteMeshes(graph, top);
        WriteBoundaries(graph);
        WriteAmbiguities(graph, top);

        if (commandLine.Option("json") is { } jsonPath)
        {
            var fullPath = Path.GetFullPath(jsonPath);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(fullPath, ShaftGraphReportBuilder.ToJson(
                ShaftGraphReportBuilder.Build(
                    graph,
                    Path.GetFileName(modelPath),
                    model.Root.Name,
                    workspace.LibraryInfo?.UpdateTag,
                    workspace.ShadowInfo?.GitCommit)));

            Console.WriteLine();
            Console.WriteLine($"Wrote {fullPath}");
        }

        return model.Expansion.Unresolved.Length == 0 ? 0 : 2;
    }

    private static void WriteMeshes(ShaftGraph graph, int top)
    {
        if (graph.Meshes.IsEmpty)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine("  Gear meshes (teeth, exact ratio, centre residual, face overlap):");
        Console.WriteLine();

        foreach (var mesh in graph.Meshes.Take(top))
        {
            var a = graph.Gears.First(gear => gear.InstanceId == mesh.GearA);
            var b = graph.Gears.First(gear => gear.InstanceId == mesh.GearB);

            var sign = mesh.Sign < 0 ? "-" : "+";
            Console.WriteLine(
                $"  {mesh.Kind,-12} {Teeth(a),-6} -> {Teeth(b),-6} "
                + $"{sign}{mesh.RatioNumerator}:{mesh.RatioDenominator,-8} "
                + $"centre {Measured(mesh.CentreDistanceLdu)} vs {Measured(mesh.ExpectedCentreDistanceLdu)} "
                + $"(residual {Measured(mesh.CentreResidualLdu)})  overlap {mesh.FaceOverlapLdu,6:F2}  {mesh.Confidence}");
            Console.WriteLine($"    {a.CanonicalPartName} [{a.ShaftId}]  <->  {b.CanonicalPartName} [{b.ShaftId}]");
            Console.WriteLine($"    rule: {mesh.Rule}");
            Console.WriteLine($"    axes: {a.AxisSource} / {b.AxisSource}, angle {mesh.AxisAngleDegrees:F2} deg");
        }

        if (graph.Meshes.Length > top)
        {
            Console.WriteLine($"    ... {graph.Meshes.Length - top:N0} more; use --top <n> or --json <file>.");
        }
    }

    private static void WriteBoundaries(ShaftGraph graph)
    {
        if (graph.UnsupportedComponents.IsEmpty)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine("  Unsupported mechanism boundaries (propagation stops here):");
        Console.WriteLine();

        foreach (var group in graph.UnsupportedComponents
            .GroupBy(component => (component.CanonicalPartName, component.Type, component.Reason))
            .OrderByDescending(group => group.Count()))
        {
            Console.WriteLine($"  {group.Count(),5}  {group.Key.CanonicalPartName,-18} {group.Key.Type}");
            Console.WriteLine($"         {group.Key.Reason}");
        }

        if (!graph.UncataloguedComponents.IsEmpty)
        {
            Console.WriteLine();
            Console.WriteLine("  Catalogued as toothed but not placeable:");
            foreach (var component in graph.UncataloguedComponents)
            {
                Console.WriteLine($"  {component.InstanceCount,5}  {component.CanonicalPartName,-18} {component.Description}");
            }
        }
    }

    private static void WriteAmbiguities(ShaftGraph graph, int top)
    {
        if (graph.AmbiguousMeshes.IsEmpty)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine("  Ambiguous meshes (confirm one in the model sidecar):");
        Console.WriteLine();

        foreach (var ambiguous in graph.AmbiguousMeshes.Take(top))
        {
            Console.WriteLine($"  {ambiguous.GearInstanceId}");
            Console.WriteLine($"    {ambiguous.Reason}");
        }

        if (graph.AmbiguousMeshes.Length > top)
        {
            Console.WriteLine($"    ... {graph.AmbiguousMeshes.Length - top:N0} more.");
        }
    }

    private static string Teeth(MountedGear gear) =>
        gear.Mechanics.Gear is { } toothed ? $"{toothed.Teeth}T"
        : gear.Mechanics.Worm is { } worm ? $"{worm.Starts}st"
        : "-";

    /// <summary>Prints an unpredicted quantity as "n/a" rather than as a suspiciously perfect 0.</summary>
    private static string Measured(float value) =>
        float.IsNaN(value) ? "   n/a" : $"{value,6:F2}";

    private static int ParseTop(string? value) =>
        int.TryParse(value, out var parsed) && parsed > 0 ? parsed : 40;

    private static string Short(string? commit) =>
        commit is null ? "(unknown)" : commit[..Math.Min(12, commit.Length)];
}

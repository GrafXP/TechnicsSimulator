using System.Collections.Immutable;
using TechnicsSim.LDraw;
using TechnicsSim.Mechanics.Catalog;
using TechnicsSim.Mechanics.Mating;
using TechnicsSim.Mechanics.Shafts;
using TechnicsSim.Mechanics.Sidecar;

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

        var useSidecar = !commandLine.Flag("no-sidecar");
        var sidecar = useSidecar
            ? ModelSidecarIo.LoadFor(modelPath)
            : ModelSidecar.Empty(Path.GetFileName(modelPath));

        var (graph, effect) = SidecarApplication.Build(analysis, model.Expansion, catalog, sidecar);

        var top = ParseTop(commandLine.Option("top"));

        Console.WriteLine($"Model  : {Path.GetFileName(modelPath)}  (root '{model.Root.Name}')");
        Console.WriteLine($"Library: {workspace.LibraryInfo?.UpdateTag ?? "(unknown)"}"
            + $"  |  Shadow: {Short(workspace.ShadowInfo?.GitCommit)}");
        Console.WriteLine($"Catalog: {catalog.Parts.Count} parts");
        Console.WriteLine($"Sidecar: {DescribeSidecar(useSidecar, sidecar, modelPath)}");
        Console.WriteLine();

        WriteSidecarEffect(effect);

        var solvedShafts = graph.Shafts.Count(shaft => shaft.MemberCount > 1);
        Console.WriteLine($"  Shaft assemblies          : {graph.Shafts.Length,8:N0}  ({solvedShafts:N0} with more than one member)");
        Console.WriteLine($"  Mounted gears             : {graph.Gears.Length,8:N0}");
        Console.WriteLine($"  Gear meshes               : {graph.Meshes.Length,8:N0}");
        Console.WriteLine($"  Ambiguous meshes          : {graph.AmbiguousMeshes.Length,8:N0}");
        Console.WriteLine($"  Unsupported components    : {graph.UnsupportedComponents.Length,8:N0}");
        Console.WriteLine($"  Uncatalogued components   : {graph.UncataloguedComponents.Length,8:N0}");
        Console.WriteLine($"  Drivers (motors)          : {graph.Drivers.Length,8:N0}");

        foreach (var driver in graph.Drivers)
        {
            Console.WriteLine($"      {driver.Label,-18} {driver.CanonicalPartName,-12} {driver.ShaftId ?? "(no keyed shaft)"}");
        }

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

        if (commandLine.Flag("export-sidecar"))
        {
            ExportSidecar(modelPath, model.Expansion, graph, sidecar);
        }

        // A stale override is a review that no longer applies to this model, which is worth a
        // non-zero exit so a build step notices rather than trusting the annotations.
        if (!effect.StaleEntries.IsEmpty)
        {
            return 3;
        }

        return model.Expansion.Unresolved.Length == 0 ? 0 : 2;
    }

    private static string DescribeSidecar(bool enabled, ModelSidecar sidecar, string modelPath)
    {
        if (!enabled)
        {
            return "disabled (--no-sidecar)";
        }

        var path = ModelSidecarIo.PathFor(modelPath);
        if (!File.Exists(path))
        {
            return $"none committed ({Path.GetFileName(path)})";
        }

        return $"{Path.GetFileName(path)}: {sidecar.Meshes.Length} mesh, "
            + $"{sidecar.Clutches.Length} clutch, {sidecar.Drivers.Length} driver entries";
    }

    private static void WriteSidecarEffect(SidecarEffect effect)
    {
        if (!effect.StaleEntries.IsEmpty)
        {
            Console.WriteLine("  Stale sidecar entries (the model changed under these overrides):");
            foreach (var entry in effect.StaleEntries)
            {
                Console.WriteLine($"    {entry.InstanceId}");
                Console.WriteLine($"      recorded {entry.Expected}, found {entry.Actual}");
            }

            Console.WriteLine();
        }

        if (!effect.ChangedAnything)
        {
            return;
        }

        Console.WriteLine("  Sidecar review applied:");
        foreach (var locked in effect.LockedClutches)
        {
            Console.WriteLine($"    clutch locked : {locked}");
        }

        foreach (var freed in effect.FreedClutches)
        {
            Console.WriteLine($"    clutch free   : {freed}");
        }

        foreach (var rejected in effect.RejectedMeshes)
        {
            Console.WriteLine($"    mesh rejected : {rejected}");
        }

        foreach (var accepted in effect.AcceptedMeshes)
        {
            Console.WriteLine($"    mesh accepted : {accepted} (not found automatically)");
        }

        Console.WriteLine();
    }

    /// <summary>
    /// Writes a sidecar skeleton seeded with what the model currently contains.
    ///
    /// Existing decisions are preserved; only the fingerprints and the list of things still
    /// awaiting review are refreshed, so exporting never silently discards a reviewer's work.
    /// </summary>
    private static void ExportSidecar(
        string modelPath,
        LDraw.Expansion.ModelExpansion expansion,
        ShaftGraph graph,
        ModelSidecar existing)
    {
        var fingerprints = SidecarApplication.Fingerprints(expansion);
        var interesting = graph.Gears.Select(gear => gear.InstanceId)
            .Concat(graph.UnsupportedComponents.Select(component => component.InstanceId))
            .Concat(graph.Drivers.Select(driver => driver.InstanceId))
            .ToHashSet(StringComparer.Ordinal);

        var exported = existing with
        {
            SchemaVersion = ModelSidecar.CurrentSchemaVersion,
            Model = Path.GetFileName(modelPath),
            InstanceFingerprints = fingerprints
                .Where(pair => interesting.Contains(pair.Key))
                .ToImmutableDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            Note = existing.Note
                ?? "Reviewed corrections for this model. Every override needs a reason. "
                    + "Fingerprints are part name and rounded world position, so a moved or "
                    + "replaced part invalidates its overrides instead of silently repointing them.",
        };

        var path = ModelSidecarIo.PathFor(modelPath);
        File.WriteAllText(path, ModelSidecarIo.ToJson(exported));

        Console.WriteLine();
        Console.WriteLine($"Wrote {Path.GetFullPath(path)}");
        Console.WriteLine($"  {exported.InstanceFingerprints.Count} fingerprints for gears and boundaries.");
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

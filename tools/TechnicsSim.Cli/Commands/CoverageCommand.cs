using TechnicsSim.LDraw;
using TechnicsSim.LDraw.Reporting;

namespace TechnicsSim.Cli.Commands;

/// <summary>
/// Reports how much of a model's mechanism-relevant data actually exists, before any of it is
/// interpreted. This is the command the Phase 0 gate is measured against, and it stays in the
/// product as a regression tool rather than being a throwaway spike.
///
/// The report itself is built in the core library so the CLI, the golden tests, and the future
/// diagnostics UI cannot drift apart. This command only chooses inputs and formats output.
/// </summary>
public static class CoverageCommand
{
    public static int Run(Workspace workspace, CommandLine commandLine)
    {
        var modelPath = commandLine.RequirePositional(0, "<model.mpd>");
        var library = workspace.RequireLibrary();
        var shadow = workspace.RequireShadow();

        var model = ModelLoader.Load(modelPath, [library], commandLine.Option("root"));
        var report = CoverageReportBuilder.Build(
            model, shadow, workspace.LibraryInfo, workspace.ShadowInfo);

        WriteConsoleSummary(report);

        if (commandLine.Option("json") is { } jsonPath)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(jsonPath));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(jsonPath, CoverageReportBuilder.ToJson(report));
            Console.WriteLine();
            Console.WriteLine($"Wrote {Path.GetFullPath(jsonPath)}");
        }

        return report.Resolution.UnresolvedCount == 0 ? 0 : 2;
    }

    private static void WriteConsoleSummary(CoverageReport report)
    {
        Console.WriteLine($"Model  : {report.Model.File}  (root '{report.Model.RootSection}')");
        Console.WriteLine($"Library: {report.Sources.OfficialLibrary?.UpdateTag ?? "(unknown)"}"
            + $"  |  Shadow: {Short(report.Sources.ShadowLibrary?.Commit)}");
        Console.WriteLine();

        var c = report.Counts;
        Console.WriteLine($"  Physical type-1 lines      : {c.PhysicalSubfileLines,7:N0}");
        Console.WriteLine($"  Logical part instances     : {c.LogicalPartInstances,7:N0}");
        Console.WriteLine($"  Distinct logical parts     : {c.DistinctLogicalParts,7:N0}");
        Console.WriteLine($"  Inline geometry lines      : {c.PhysicalInlineGeometryLines.Values.Sum(),7:N0}"
            + $" physical, {c.ExpandedInlineGeometryLines.Values.Sum():N0} expanded");
        Console.WriteLine();

        Console.WriteLine($"  Unresolved references      : {report.Resolution.UnresolvedCount,7:N0}");
        Console.WriteLine($"  Ambiguous references       : {report.Resolution.AmbiguousCount,7:N0}");
        Console.WriteLine($"  Parse issues               : {report.Resolution.ParseIssueCount,7:N0}");
        Console.WriteLine();

        var s = report.Shadow;
        Console.WriteLine("  Shadow coverage        unique parts   weighted instances");
        WriteCoverageRow("direct", s.ByUniquePart.Direct, s.ByInstance.Direct);
        WriteCoverageRow("direct + inherited", s.ByUniquePart.DirectAndInherited, s.ByInstance.DirectAndInherited);
        WriteCoverageRow("inherited only", s.ByUniquePart.Inherited, s.ByInstance.Inherited);
        WriteCoverageRow("none", s.ByUniquePart.None, s.ByInstance.None);
        Console.WriteLine();

        Console.WriteLine("  Feature types found (reachability; use 'connections' for effective semantics):");
        foreach (var (type, count) in s.FeatureTypeHistogram)
        {
            Console.WriteLine($"    {type,-12} {count,7:N0}");
        }

        Console.WriteLine();
        Console.WriteLine($"  SNAP_CLEAR metas seen      : {s.SnapClearCount,7:N0}  "
            + "(reachability totals; the effective extractor applies them)");
        Console.WriteLine($"  Mirrored child references  : {s.MirroredChildReferences,7:N0}  "
            + "(the effective extractor decides each)");
        Console.WriteLine($"  Scaled child references    : {s.ScaledChildReferences,7:N0}  "
            + "(the effective extractor decides each)");

        if (s.HighUseUncoveredParts.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  Highest-use parts with no shadow features:");
            foreach (var part in s.HighUseUncoveredParts.Take(15))
            {
                Console.WriteLine($"    {part.Instances,6:N0}  {part.Part}");
            }
        }
    }

    private static void WriteCoverageRow(string label, int parts, int instances) =>
        Console.WriteLine($"    {label,-22} {parts,7:N0}   {instances,17:N0}");

    private static string Short(string? commit) =>
        commit is null ? "(unknown)" : commit[..Math.Min(12, commit.Length)];
}

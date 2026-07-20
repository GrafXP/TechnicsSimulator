using TechnicsSim.LDraw;
using TechnicsSim.LDraw.Ast;

namespace TechnicsSim.Cli.Commands;

/// <summary>
/// Summarizes a model's structure: sections, the three distinct counts, resolution failures,
/// and the parts that dominate the instance total.
/// </summary>
public static class InspectModelCommand
{
    public static int Run(Workspace workspace, CommandLine commandLine)
    {
        var modelPath = commandLine.RequirePositional(0, "<model.mpd>");
        var library = workspace.RequireLibrary();

        var model = ModelLoader.Load(modelPath, [library], commandLine.Option("root"));
        var expansion = model.Expansion;

        Console.WriteLine($"Model            : {model.Path}");
        Console.WriteLine($"Root section     : {model.Root.Name}");
        Console.WriteLine($"MPD sections     : {model.Sections.Length:N0}");
        Console.WriteLine();

        // These three numbers describe different things and are kept separate on purpose.
        Console.WriteLine($"Physical type-1 lines        : {model.PhysicalSubfileLineCount:N0}");
        Console.WriteLine($"Expanded logical instances   : {expansion.Instances.Length:N0}");
        Console.WriteLine($"Distinct logical parts       : {expansion.PartUsage.Count:N0}");
        Console.WriteLine($"Submodel references followed : {expansion.SubmodelReferenceCount:N0}");
        Console.WriteLine();

        var physicalGeometry = model.PhysicalGeometryLineCounts;
        if (physicalGeometry.Count > 0)
        {
            Console.WriteLine("Inline geometry in model sections (generated hose/spring fallback meshes):");
            Console.WriteLine("  type  physical   expanded");
            foreach (var type in physicalGeometry.Keys.Order())
            {
                Console.WriteLine(
                    $"  {type,4}  {physicalGeometry[type],8:N0}  "
                    + $"{expansion.ExpandedInlineGeometryLines.GetValueOrDefault(type),9:N0}");
            }

            Console.WriteLine(
                $"  all   {physicalGeometry.Values.Sum(),8:N0}  "
                + $"{expansion.TotalExpandedInlineGeometryLines,9:N0}");
            Console.WriteLine();
        }

        var mpdParts = model.Sections
            .Where(s => s.OrgKind is LDrawOrgKind.Part or LDrawOrgKind.Shortcut)
            .ToList();

        if (mpdParts.Count > 0)
        {
            // These are the reason a `.dat` suffix cannot be trusted to mean "library part".
            Console.WriteLine("MPD-embedded parts (resolve before the official library):");
            foreach (var part in mpdParts)
            {
                var uses = expansion.PartUsage.GetValueOrDefault(part.CanonicalName);
                Console.WriteLine(
                    $"  {part.Name}  [{(part.IsUnofficial ? "Unofficial_" : string.Empty)}{part.OrgKind}]"
                    + $"  {uses:N0} instances");
            }

            Console.WriteLine();
        }

        Console.WriteLine("Top parts by instance count:");
        var limit = int.TryParse(commandLine.Option("top"), out var top) ? top : 15;
        foreach (var (part, count) in expansion.PartUsage.OrderByDescending(p => p.Value).Take(limit))
        {
            Console.WriteLine($"  {count,6:N0}  {part}");
        }

        Console.WriteLine();
        Report("Non-part references from a model context", expansion.NonPartReferences.Select(
            n => $"{n.RequestedName} [{n.Kind}, from {n.Origin}] at {n.DeclaringDocument}:{n.LineNumber}"));
        Report("Unresolved references", expansion.Unresolved.Select(
            u => $"{u.RequestedName} [{u.Failure}] at {u.DeclaringDocument}:{u.LineNumber}"));
        Report("Ambiguous references", expansion.AmbiguousReferences);
        Report("Parse issues", model.ParseIssues.Select(
            i => $"{i.DocumentName}:{i.LineNumber} {i.Reason}  |  {i.Line}"));

        return expansion.Unresolved.Length == 0 ? 0 : 2;
    }

    private static void Report(string title, IEnumerable<string> entries)
    {
        var list = entries.ToList();
        if (list.Count == 0)
        {
            Console.WriteLine($"{title}: none");
            return;
        }

        Console.WriteLine($"{title}: {list.Count:N0}");
        foreach (var entry in list.Take(25))
        {
            Console.WriteLine($"  {entry}");
        }

        if (list.Count > 25)
        {
            Console.WriteLine($"  ... and {list.Count - 25:N0} more");
        }
    }
}

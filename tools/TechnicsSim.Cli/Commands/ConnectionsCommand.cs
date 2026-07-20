using TechnicsSim.LDraw;
using TechnicsSim.Mechanics.Mating;
using TechnicsSim.Mechanics.Reporting;

namespace TechnicsSim.Cli.Commands;

public static class ConnectionsCommand
{
    public static int Run(Workspace workspace, CommandLine commandLine)
    {
        var modelPath = commandLine.RequirePositional(0, "<model.mpd>");
        var model = ModelLoader.Load(modelPath, [workspace.RequireLibrary()], commandLine.Option("root"));
        var analysis = ModelConnectionAnalyzer.Analyze(model, workspace.RequireShadow());
        var report = ConnectionGraphReportBuilder.Build(
            model,
            analysis,
            workspace.LibraryInfo?.UpdateTag ?? workspace.LibraryInfo?.Sha256,
            workspace.ShadowInfo?.GitCommit);

        WriteSummary(report, ParseTop(commandLine.Option("top")));

        if (commandLine.Option("json") is { } jsonPath)
        {
            var fullPath = Path.GetFullPath(jsonPath);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(fullPath, ConnectionGraphReportBuilder.ToJson(report));
            Console.WriteLine();
            Console.WriteLine($"Wrote {fullPath}");
        }

        return model.Expansion.Unresolved.Length == 0 ? 0 : 2;
    }

    private static void WriteSummary(ConnectionGraphReport report, int top)
    {
        Console.WriteLine($"Model  : {report.Model.File}  (root '{report.Model.RootSection}')");
        Console.WriteLine($"Library: {report.Sources.OfficialLibraryVersion ?? "(unknown)"}"
            + $"  |  Shadow: {Short(report.Sources.ShadowCommit)}");
        Console.WriteLine();
        Console.WriteLine($"  Logical instances         : {report.Counts.LogicalInstances,8:N0}");
        Console.WriteLine($"  Effective placed features : {report.Counts.Features,8:N0}");
        Console.WriteLine($"  Broad-phase AABB pairs    : {report.Counts.BroadPhasePairs,8:N0}");
        Console.WriteLine($"  Narrow-phase candidates   : {report.Counts.NarrowPhaseCandidates,8:N0}");
        Console.WriteLine($"  Connection candidates     : {report.Counts.Connections,8:N0}");
        Console.WriteLine($"  Unmatched features        : {report.Counts.UnmatchedFeatures,8:N0}");
        Console.WriteLine($"  Ambiguous features        : {report.Counts.AmbiguousFeatures,8:N0}");
        Console.WriteLine($"  Extraction issues         : {report.ExtractionIssues.Length,8:N0}");
        Console.WriteLine($"  Rejected inheritance      : {report.RejectedInheritance.Length,8:N0}");

        if (!report.Connections.IsEmpty)
        {
            Console.WriteLine();
            Console.WriteLine("  kind                 confidence  residuals (radial / overlap / angle)  instances");
            foreach (var connection in report.Connections.Take(top))
            {
                var ambiguity = connection.Ambiguous ? " ambiguous" : string.Empty;
                Console.WriteLine(
                    $"  {connection.Kind,-20} {connection.Confidence,-10} "
                    + $"{connection.Residuals.RadialLdu,6:F3} / {connection.Residuals.AxialOverlapLdu,7:F3} / "
                    + $"{connection.Residuals.AxisAngleDegrees,5:F2}  "
                    + $"{connection.InstanceA} <-> {connection.InstanceB}{ambiguity}");
                Console.WriteLine($"    rule: {connection.Rule}");
            }

            if (report.Connections.Length > top)
            {
                Console.WriteLine($"    ... {report.Connections.Length - top:N0} more; use --top <n> or --json <file>.");
            }
        }
    }

    private static int ParseTop(string? value) =>
        int.TryParse(value, out var parsed) && parsed > 0 ? parsed : 50;

    private static string Short(string? commit) =>
        commit is null ? "(unknown)" : commit[..Math.Min(12, commit.Length)];
}

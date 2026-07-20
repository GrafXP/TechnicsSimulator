using TechnicsSim.LDraw;
using TechnicsSim.Mechanics.Catalog;
using TechnicsSim.Mechanics.Mating;
using TechnicsSim.Mechanics.Shafts;
using TechnicsSim.Mechanics.Sidecar;
using TechnicsSim.Mechanics.Solver;

namespace TechnicsSim.Cli.Commands;

/// <summary>Solves the same reviewed shaft graph the viewer animates.</summary>
public static class SolveCommand
{
    public static int Run(Workspace workspace, CommandLine commandLine)
    {
        var modelPath = commandLine.RequirePositional(0, "<model.mpd>");
        var model = ModelLoader.Load(modelPath, [workspace.RequireLibrary()], commandLine.Option("root"));
        var analysis = ModelConnectionAnalyzer.Analyze(model, workspace.RequireShadow());
        var catalog = CatalogLocator.Load(commandLine.Option("catalog"));
        var sidecar = commandLine.Flag("no-sidecar")
            ? ModelSidecar.Empty(Path.GetFileName(modelPath))
            : ModelSidecarIo.LoadFor(modelPath);
        var (graph, effect) = SidecarApplication.Build(analysis, model.Expansion, catalog, sidecar);

        var inputs = SelectInputs(graph, sidecar, commandLine.Option("driver"));
        var solution = ShaftSolver.Solve(graph, inputs);

        Console.WriteLine($"Model  : {Path.GetFileName(modelPath)}");
        Console.WriteLine($"Inputs : {inputs.Count:N0}");
        foreach (var input in inputs)
        {
            Console.WriteLine($"  {input.Label} [{input.ShaftId}] = {input.AngularVelocity}");
        }

        Console.WriteLine();
        Console.WriteLine($"Solved : {solution.Shafts.Length:N0} / {graph.Shafts.Length:N0} shafts");
        foreach (var shaft in solution.Shafts)
        {
            Console.WriteLine(
                $"  {shaft.ShaftId,-16} {shaft.AngularVelocity,10}  "
                + $"from {shaft.InputLabel} ({shaft.Path.Length} mesh{(shaft.Path.Length == 1 ? string.Empty : "es")})");
        }

        if (!solution.Conflicts.IsEmpty)
        {
            Console.WriteLine();
            Console.WriteLine("Conflicts:");
            foreach (var conflict in solution.Conflicts)
            {
                Console.WriteLine($"  {conflict.Message}");
                Console.WriteLine(
                    $"    {conflict.ExistingInput}: {conflict.ExistingVelocity} via "
                    + $"{DescribePath(conflict.ExistingPath)}");
                Console.WriteLine(
                    $"    {conflict.ProposedInput}: {conflict.ProposedVelocity} via "
                    + $"{DescribePath(conflict.ProposedPath)}");
            }
        }

        if (commandLine.Option("json") is { } jsonPath)
        {
            var fullPath = Path.GetFullPath(jsonPath);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(fullPath, ShaftSolutionReportBuilder.ToJson(
                ShaftSolutionReportBuilder.Build(Path.GetFileName(modelPath), inputs, solution)));
            Console.WriteLine();
            Console.WriteLine($"Wrote {fullPath}");
        }

        if (!effect.StaleEntries.IsEmpty)
        {
            return 3;
        }

        if (!solution.IsConsistent)
        {
            return 4;
        }

        return model.Expansion.Unresolved.Length == 0 ? 0 : 2;
    }

    private static List<ShaftInput> SelectInputs(
        ShaftGraph graph,
        ModelSidecar sidecar,
        string? requestedDriver)
    {
        IEnumerable<(string InstanceId, string Label)> selected;

        if (requestedDriver is not null)
        {
            var matches = graph.Drivers.Where(driver =>
                    string.Equals(driver.InstanceId, requestedDriver, StringComparison.Ordinal)
                    || string.Equals(driver.Label, requestedDriver, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(driver.CanonicalPartName, requestedDriver, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count != 1)
            {
                throw new CommandLineException(
                    matches.Count == 0
                        ? $"Driver '{requestedDriver}' was not found in the graph."
                        : $"Driver '{requestedDriver}' is ambiguous; pass its full instance id.");
            }

            selected = [(matches[0].InstanceId, matches[0].Label)];
        }
        else if (!sidecar.Drivers.IsEmpty)
        {
            selected = sidecar.Drivers.Select(driver => (driver.InstanceId, driver.Label));
        }
        else
        {
            var first = graph.Drivers.FirstOrDefault(driver => driver.ShaftId is not null)
                ?? throw new CommandLineException("The graph contains no motor attached to a shaft.");
            selected = [(first.InstanceId, first.Label)];
        }

        var inputs = new List<ShaftInput>();
        foreach (var (instanceId, label) in selected)
        {
            var driver = graph.Drivers.FirstOrDefault(candidate => candidate.InstanceId == instanceId)
                ?? throw new CommandLineException($"Reviewed driver '{label}' no longer exists in the graph.");
            if (driver.ShaftId is null)
            {
                throw new CommandLineException($"Driver '{label}' has no keyed output shaft.");
            }

            inputs.Add(new ShaftInput(driver.ShaftId, ExactRatio.One, label));
        }

        return inputs;
    }

    private static string DescribePath(IReadOnlyCollection<PropagationStep> path) =>
        path.Count == 0
            ? "the input shaft"
            : string.Join(" -> ", path.Select(step => $"{step.ToShaft} ({step.Multiplier})"));
}

using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TechnicsSim.Mechanics.Solver;

public sealed record ShaftInputReport(string ShaftId, string Label, string AngularVelocity);

public sealed record SolvedShaftReport(
    string ShaftId,
    string AngularVelocity,
    double AngularVelocityValue,
    string InputLabel,
    int PathLength);

public sealed record ShaftConflictReport(
    string ShaftId,
    string ExistingVelocity,
    string ProposedVelocity,
    string ExistingInput,
    string ProposedInput,
    string Message,
    ImmutableArray<PropagationStep> ExistingPath,
    ImmutableArray<PropagationStep> ProposedPath);

public sealed record ShaftSolutionReport(
    int SchemaVersion,
    string Model,
    ImmutableArray<ShaftInputReport> Inputs,
    ImmutableArray<SolvedShaftReport> Shafts,
    ImmutableArray<string> UnsolvedShaftIds,
    ImmutableArray<ShaftConflictReport> Conflicts);

public static class ShaftSolutionReportBuilder
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static ShaftSolutionReport Build(
        string model,
        IReadOnlyCollection<ShaftInput> inputs,
        ShaftSolution solution) => new(
        SchemaVersion: 1,
        Model: model,
        Inputs:
        [
            .. inputs.Select(input => new ShaftInputReport(
                input.ShaftId,
                input.Label,
                input.AngularVelocity.ToString())),
        ],
        Shafts:
        [
            .. solution.Shafts.Select(shaft => new SolvedShaftReport(
                shaft.ShaftId,
                shaft.AngularVelocity.ToString(),
                shaft.AngularVelocity.ToDouble(),
                shaft.InputLabel,
                shaft.Path.Length)),
        ],
        UnsolvedShaftIds: solution.UnsolvedShaftIds,
        Conflicts:
        [
            .. solution.Conflicts.Select(conflict => new ShaftConflictReport(
                conflict.ShaftId,
                conflict.ExistingVelocity.ToString(),
                conflict.ProposedVelocity.ToString(),
                conflict.ExistingInput,
                conflict.ProposedInput,
                conflict.Message,
                conflict.ExistingPath,
                conflict.ProposedPath)),
        ]);

    public static string ToJson(ShaftSolutionReport report) =>
        JsonSerializer.Serialize(report, JsonOptions);
}

using System.Collections.Immutable;
using TechnicsSim.Mechanics.Shafts;

namespace TechnicsSim.Mechanics.Solver;

/// <summary>A prescribed angular velocity for one shaft.</summary>
public sealed record ShaftInput(string ShaftId, ExactRatio AngularVelocity, string Label);

/// <summary>One exact gear relation used to reach a shaft from an input.</summary>
public sealed record PropagationStep(
    string FromShaft,
    string ToShaft,
    ExactRatio Multiplier,
    string GearA,
    string GearB);

/// <summary>The solved angular velocity of one shaft and the path that justified it.</summary>
public sealed record SolvedShaft(
    string ShaftId,
    ExactRatio AngularVelocity,
    string InputLabel,
    ImmutableArray<PropagationStep> Path);

/// <summary>
/// Two exact constraints assigned different velocities to the same shaft.
/// Both paths are retained so a diagnostic says how each answer was reached.
/// </summary>
public sealed record ShaftConflict(
    string ShaftId,
    ExactRatio ExistingVelocity,
    ExactRatio ProposedVelocity,
    string ExistingInput,
    string ProposedInput,
    ImmutableArray<PropagationStep> ExistingPath,
    ImmutableArray<PropagationStep> ProposedPath,
    string Message);

public sealed record ShaftSolution(
    ImmutableArray<SolvedShaft> Shafts,
    ImmutableArray<string> UnsolvedShaftIds,
    ImmutableArray<ShaftConflict> Conflicts)
{
    public bool IsConsistent => Conflicts.IsEmpty;

    public SolvedShaft? Find(string shaftId) =>
        Shafts.FirstOrDefault(shaft => shaft.ShaftId == shaftId);
}

/// <summary>
/// Propagates exact angular-velocity constraints over the pairwise gear graph.
///
/// Inputs are seeded simultaneously, so several motors are first-class constraints rather than
/// one motor silently winning by iteration order. Encountering an already-solved shaft checks
/// exact equality; a mismatch preserves both derivations as a conflict and never overwrites a
/// valid earlier state.
/// </summary>
public static class ShaftSolver
{
    public static ShaftSolution Solve(ShaftGraph graph, IEnumerable<ShaftInput> inputs)
    {
        var adjacency = BuildAdjacency(graph);
        var states = new Dictionary<string, SolvedShaft>(StringComparer.Ordinal);
        var queue = new Queue<SolvedShaft>();
        var conflicts = ImmutableArray.CreateBuilder<ShaftConflict>();
        var conflictKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var input in inputs.OrderBy(input => input.ShaftId, StringComparer.Ordinal)
                     .ThenBy(input => input.Label, StringComparer.Ordinal))
        {
            if (graph.FindShaft(input.ShaftId) is null)
            {
                throw new ArgumentException($"Input '{input.Label}' names unknown shaft '{input.ShaftId}'.", nameof(inputs));
            }

            var proposed = new SolvedShaft(input.ShaftId, input.AngularVelocity, input.Label, []);
            if (!states.TryGetValue(input.ShaftId, out var existing))
            {
                states[input.ShaftId] = proposed;
                queue.Enqueue(proposed);
            }
            else if (existing.AngularVelocity != proposed.AngularVelocity)
            {
                AddConflict(
                    $"input:{input.ShaftId}", existing, proposed,
                    $"Inputs '{existing.InputLabel}' and '{proposed.InputLabel}' prescribe "
                    + $"{existing.AngularVelocity} and {proposed.AngularVelocity} on {input.ShaftId}.");
            }
        }

        while (queue.TryDequeue(out var current))
        {
            foreach (var edge in adjacency.GetValueOrDefault(current.ShaftId, []))
            {
                var path = current.Path.Add(new PropagationStep(
                    current.ShaftId,
                    edge.ToShaft,
                    edge.Multiplier,
                    edge.Mesh.GearA,
                    edge.Mesh.GearB));
                var proposed = new SolvedShaft(
                    edge.ToShaft,
                    current.AngularVelocity * edge.Multiplier,
                    current.InputLabel,
                    path);

                if (!states.TryGetValue(edge.ToShaft, out var existing))
                {
                    states[edge.ToShaft] = proposed;
                    queue.Enqueue(proposed);
                    continue;
                }

                if (existing.AngularVelocity != proposed.AngularVelocity)
                {
                    var key = MeshKey(edge.Mesh);
                    AddConflict(
                        key,
                        existing,
                        proposed,
                        $"Gear relation {edge.Mesh.GearA} <-> {edge.Mesh.GearB} requires "
                        + $"{edge.ToShaft} to be {proposed.AngularVelocity}, but it is already "
                        + $"{existing.AngularVelocity}.");
                }
            }
        }

        return new ShaftSolution(
            [.. states.Values.OrderBy(state => state.ShaftId, StringComparer.Ordinal)],
            [.. graph.Shafts.Select(shaft => shaft.ShaftId)
                .Where(shaftId => !states.ContainsKey(shaftId))
                .OrderBy(shaftId => shaftId, StringComparer.Ordinal)],
            conflicts.ToImmutable());

        void AddConflict(string key, SolvedShaft existing, SolvedShaft proposed, string message)
        {
            if (!conflictKeys.Add(key))
            {
                return;
            }

            conflicts.Add(new ShaftConflict(
                proposed.ShaftId,
                existing.AngularVelocity,
                proposed.AngularVelocity,
                existing.InputLabel,
                proposed.InputLabel,
                existing.Path,
                proposed.Path,
                message));
        }
    }

    private static Dictionary<string, List<Edge>> BuildAdjacency(ShaftGraph graph)
    {
        var result = new Dictionary<string, List<Edge>>(StringComparer.Ordinal);

        foreach (var mesh in graph.Meshes.OrderBy(MeshKey, StringComparer.Ordinal))
        {
            var forward = new ExactRatio(mesh.Sign * mesh.RatioNumerator, mesh.RatioDenominator);
            Add(mesh.ShaftA, new Edge(mesh.ShaftB, forward, mesh));
            Add(mesh.ShaftB, new Edge(mesh.ShaftA, forward.Reciprocal(), mesh));
        }

        return result;

        void Add(string from, Edge edge)
        {
            if (!result.TryGetValue(from, out var list))
            {
                result[from] = list = [];
            }

            list.Add(edge);
        }
    }

    private static string MeshKey(GearMesh mesh) =>
        string.CompareOrdinal(mesh.GearA, mesh.GearB) <= 0
            ? $"mesh:{mesh.GearA}|{mesh.GearB}"
            : $"mesh:{mesh.GearB}|{mesh.GearA}";

    private sealed record Edge(string ToShaft, ExactRatio Multiplier, GearMesh Mesh);
}

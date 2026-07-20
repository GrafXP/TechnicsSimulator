using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using TechnicsSim.Mechanics.Shafts;
using TechnicsSim.Mechanics.Solver;

namespace TechnicsSim.Wpf.ViewModels;

/// <summary>An item in the solution view that can locate its mechanical parts in 3D.</summary>
public interface IDrivetrainSolutionItem
{
    ImmutableArray<string> HighlightInstanceIds { get; }
}

/// <summary>One solved shaft in a driver-rooted propagation tree.</summary>
public sealed class SolvedShaftNode(
    string headline,
    string detail,
    ImmutableArray<string> highlightInstanceIds) : IDrivetrainSolutionItem
{
    public string Headline { get; } = headline;

    public string Detail { get; } = detail;

    public ImmutableArray<string> HighlightInstanceIds { get; } = highlightInstanceIds;

    public ObservableCollection<SolvedShaftNode> Children { get; } = [];

    public bool IsExpanded { get; set; } = true;
}

/// <summary>One graph edge, whether or not the selected inputs reached it.</summary>
public sealed record DrivetrainConstraintRow(
    string Headline,
    string Parts,
    string Status,
    ImmutableArray<string> HighlightInstanceIds) : IDrivetrainSolutionItem;

/// <summary>An inconsistent assignment with both exact derivations retained.</summary>
public sealed record DrivetrainConflictRow(
    string Message,
    string ExistingPath,
    string ProposedPath,
    ImmutableArray<string> HighlightInstanceIds) : IDrivetrainSolutionItem;

/// <summary>An unreached graph node that can still be selected as a manual input.</summary>
public sealed record UnsolvedShaftRow(
    string ShaftId,
    string Detail,
    ImmutableArray<string> HighlightInstanceIds) : IDrivetrainSolutionItem;

/// <summary>
/// A readable graph projection of the exact solution: driver-rooted trees for propagation,
/// a complete edge list, conflicts, and explicitly unreached shafts.
/// </summary>
public sealed class DrivetrainSolutionViewModel : INotifyPropertyChanged
{
    private readonly Action<ImmutableArray<string>> _highlight;
    private string _summary = "Load a model to inspect its drivetrain graph.";

    public DrivetrainSolutionViewModel(Action<ImmutableArray<string>> highlight) =>
        _highlight = highlight;

    public ObservableCollection<SolvedShaftNode> Roots { get; } = [];

    public ObservableCollection<DrivetrainConstraintRow> Constraints { get; } = [];

    public ObservableCollection<DrivetrainConflictRow> Conflicts { get; } = [];

    public ObservableCollection<UnsolvedShaftRow> UnsolvedShafts { get; } = [];

    public string Summary
    {
        get => _summary;
        private set
        {
            if (_summary == value)
            {
                return;
            }

            _summary = value;
            OnPropertyChanged();
        }
    }

    public string SolvedHeader => $"Solved drivetrain graph ({SolvedCount:N0} shafts)";

    public string ConstraintHeader => $"Gear constraints ({Constraints.Count:N0})";

    public string ConflictHeader => $"Conflicts ({Conflicts.Count:N0})";

    public string UnsolvedHeader => $"Unsolved shafts ({UnsolvedShafts.Count:N0})";

    public int SolvedCount { get; private set; }

    public void Clear(string? summary = null)
    {
        Roots.Clear();
        Constraints.Clear();
        Conflicts.Clear();
        UnsolvedShafts.Clear();
        SolvedCount = 0;
        Summary = summary ?? "Load a model to inspect its drivetrain graph.";
        NotifyCounts();
    }

    public void Load(ShaftGraph graph, ShaftSolution? solution)
    {
        Clear();

        var states = solution?.Shafts.ToDictionary(state => state.ShaftId, StringComparer.Ordinal)
            ?? new Dictionary<string, SolvedShaft>(StringComparer.Ordinal);
        var gears = graph.Gears.ToDictionary(gear => gear.InstanceId, StringComparer.Ordinal);
        var nodes = new Dictionary<string, SolvedShaftNode>(StringComparer.Ordinal);

        foreach (var state in states.Values.OrderBy(state => state.ShaftId, StringComparer.Ordinal))
        {
            var shaft = graph.FindShaft(state.ShaftId);
            var detail = state.Path.IsEmpty
                ? $"input: {state.InputLabel}"
                : DescribeStep(state.Path[^1], gears);
            nodes[state.ShaftId] = new SolvedShaftNode(
                $"{state.ShaftId}   omega {Signed(state.AngularVelocity)} x input",
                detail,
                shaft?.InstanceIds ?? []);
        }

        foreach (var state in states.Values.OrderBy(state => state.ShaftId, StringComparer.Ordinal))
        {
            var node = nodes[state.ShaftId];
            if (!state.Path.IsEmpty
                && nodes.TryGetValue(state.Path[^1].FromShaft, out var parent)
                && !ReferenceEquals(parent, node))
            {
                parent.Children.Add(node);
            }
            else
            {
                Roots.Add(node);
            }
        }

        foreach (var mesh in graph.Meshes.OrderBy(mesh => mesh.ShaftA, StringComparer.Ordinal)
                     .ThenBy(mesh => mesh.ShaftB, StringComparer.Ordinal))
        {
            var multiplier = new ExactRatio(mesh.Sign * mesh.RatioNumerator, mesh.RatioDenominator);
            var a = solution?.Find(mesh.ShaftA);
            var b = solution?.Find(mesh.ShaftB);
            var active = a is not null && b is not null;
            var consistent = active && b!.AngularVelocity == a!.AngularVelocity * multiplier;
            var partA = gears.GetValueOrDefault(mesh.GearA)?.CanonicalPartName ?? mesh.GearA;
            var partB = gears.GetValueOrDefault(mesh.GearB)?.CanonicalPartName ?? mesh.GearB;

            Constraints.Add(new DrivetrainConstraintRow(
                $"{mesh.ShaftA} -> {mesh.ShaftB}   x {Signed(multiplier)}",
                $"{partA} <-> {partB}   {mesh.Kind}",
                !active
                    ? "not reached by the selected input"
                    : consistent
                        ? $"active: {Signed(a!.AngularVelocity)} -> {Signed(b!.AngularVelocity)}"
                        : $"CONFLICT: expected {Signed(a!.AngularVelocity * multiplier)}, "
                          + $"assigned {Signed(b!.AngularVelocity)}",
                [mesh.GearA, mesh.GearB]));
        }

        if (solution is not null)
        {
            foreach (var conflict in solution.Conflicts)
            {
                var shaftInstances = graph.FindShaft(conflict.ShaftId)?.InstanceIds ?? [];
                var pathInstances = conflict.ExistingPath.Concat(conflict.ProposedPath)
                    .SelectMany(step => new[] { step.GearA, step.GearB });
                Conflicts.Add(new DrivetrainConflictRow(
                    conflict.Message,
                    $"{conflict.ExistingInput}: {Signed(conflict.ExistingVelocity)} via "
                        + DescribePath(conflict.ExistingPath),
                    $"{conflict.ProposedInput}: {Signed(conflict.ProposedVelocity)} via "
                        + DescribePath(conflict.ProposedPath),
                    [.. shaftInstances.Concat(pathInstances).Distinct(StringComparer.Ordinal)]));
            }
        }

        var unsolved = solution?.UnsolvedShaftIds
            ?? [.. graph.Shafts.Select(shaft => shaft.ShaftId).Order(StringComparer.Ordinal)];
        foreach (var shaftId in unsolved)
        {
            var shaft = graph.FindShaft(shaftId);
            UnsolvedShafts.Add(new UnsolvedShaftRow(
                shaftId,
                shaft is null ? "shaft details unavailable" : $"{shaft.InstanceIds.Length:N0} member parts",
                shaft?.InstanceIds ?? []));
        }

        SolvedCount = states.Count;
        var activeConstraints = Constraints.Count(row => row.Status.StartsWith("active", StringComparison.Ordinal));
        Summary = solution is null
            ? $"Graph built: {graph.Shafts.Length:N0} shafts and {graph.Meshes.Length:N0} gear constraints. "
              + "No driver input is selected."
            : $"{SolvedCount:N0} / {graph.Shafts.Length:N0} shafts solved   "
              + $"{activeConstraints:N0} active constraints   {Conflicts.Count:N0} conflicts";
        NotifyCounts();
    }

    public void Select(IDrivetrainSolutionItem item)
    {
        if (!item.HighlightInstanceIds.IsEmpty)
        {
            _highlight(item.HighlightInstanceIds);
        }
    }

    private static string DescribeStep(
        PropagationStep step,
        IReadOnlyDictionary<string, MountedGear> gears)
    {
        var a = gears.GetValueOrDefault(step.GearA)?.CanonicalPartName ?? step.GearA;
        var b = gears.GetValueOrDefault(step.GearB)?.CanonicalPartName ?? step.GearB;
        return $"via {a} <-> {b}   x {Signed(step.Multiplier)}";
    }

    private static string DescribePath(IReadOnlyCollection<PropagationStep> path) =>
        path.Count == 0
            ? "the input shaft"
            : string.Join(" -> ", path.Select(step => $"{step.ToShaft} ({Signed(step.Multiplier)})"));

    private static string Signed(ExactRatio ratio) => ratio.Numerator.Sign >= 0
        ? $"+{ratio}"
        : ratio.ToString();

    private void NotifyCounts()
    {
        OnPropertyChanged(nameof(SolvedCount));
        OnPropertyChanged(nameof(SolvedHeader));
        OnPropertyChanged(nameof(ConstraintHeader));
        OnPropertyChanged(nameof(ConflictHeader));
        OnPropertyChanged(nameof(UnsolvedHeader));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

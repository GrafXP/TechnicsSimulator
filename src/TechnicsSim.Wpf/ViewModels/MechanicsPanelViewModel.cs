using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using TechnicsSim.LDraw.Expansion;
using TechnicsSim.Mechanics.Catalog;
using TechnicsSim.Mechanics.Shafts;
using TechnicsSim.Mechanics.Sidecar;

namespace TechnicsSim.Wpf.ViewModels;

/// <summary>
/// The reviewable drivetrain: meshes, clutches, drivers, and the boundaries the graph refuses
/// to cross, plus the edits that turn them into a committed sidecar.
///
/// Export writes the same <c>Models/&lt;model&gt;.mechanics.json</c> the CLI reads, so a review
/// done here and a report produced in a build agree by construction rather than by convention.
/// </summary>
public sealed class MechanicsPanelViewModel : INotifyPropertyChanged
{
    private readonly Action<ImmutableArray<string>> _highlight;

    private ShaftGraph? _graph;
    private ModelExpansion? _expansion;
    private ModelSidecar _sidecar = ModelSidecar.Empty("(none)");
    private string? _modelPath;
    private MechanicsRow? _selectedRow;
    private string _exportStatus = string.Empty;

    public MechanicsPanelViewModel(Action<ImmutableArray<string>> highlight)
    {
        _highlight = highlight;
        ExportCommand = new RelayCommand(Export, () => _modelPath is not null);
        RevertCommand = new RelayCommand(Revert, () => _modelPath is not null);
    }

    public ObservableCollection<MeshRow> Meshes { get; } = [];

    public ObservableCollection<ClutchRow> Clutches { get; } = [];

    public ObservableCollection<DriverRow> Drivers { get; } = [];

    public ObservableCollection<BoundaryRow> Boundaries { get; } = [];

    public ObservableCollection<string> StaleWarnings { get; } = [];

    public RelayCommand ExportCommand { get; }

    public RelayCommand RevertCommand { get; }

    public bool HasModel => _graph is not null;

    public bool HasStaleWarnings => StaleWarnings.Count > 0;

    public string Summary => _graph is null
        ? "Load a model to review its drivetrain."
        : $"{_graph.Shafts.Length:N0} shafts   {_graph.Gears.Length:N0} gears   "
          + $"{_graph.Meshes.Length:N0} meshes   {_graph.UnsupportedComponents.Length:N0} boundaries   "
          + $"{_graph.Drivers.Length:N0} motors";

    public string ExportStatus
    {
        get => _exportStatus;
        private set => Set(ref _exportStatus, value);
    }

    /// <summary>
    /// Selecting a row drives the 3D highlight, matching the model tree's behaviour.
    ///
    /// The four sections are separate lists but share one selection, so the selected flag is
    /// maintained here rather than by any list control. That also avoids the alternative --
    /// four <c>ListBox</c>es bound to one property -- where each list that does not contain the
    /// new row reports its own selection as null and wipes the one just made.
    /// </summary>
    public MechanicsRow? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (ReferenceEquals(_selectedRow, value))
            {
                return;
            }

            if (_selectedRow is not null)
            {
                _selectedRow.IsSelected = false;
            }

            _selectedRow = value;

            if (_selectedRow is not null)
            {
                _selectedRow.IsSelected = true;
            }

            OnPropertyChanged();

            if (value is not null)
            {
                _highlight(value.HighlightInstanceIds);
            }
        }
    }

    /// <summary>Rebuilds the panel from a freshly analysed model.</summary>
    public void Load(
        string modelPath,
        ShaftGraph graph,
        ModelExpansion expansion,
        ModelSidecar sidecar,
        SidecarEffect effect)
    {
        _modelPath = modelPath;
        _graph = graph;
        _expansion = expansion;
        _sidecar = sidecar;

        // The rows about to be discarded are the ones the selection points at.
        SelectedRow = null;

        Meshes.Clear();
        Clutches.Clear();
        Drivers.Clear();
        Boundaries.Clear();
        StaleWarnings.Clear();

        var gearsById = graph.Gears.ToDictionary(gear => gear.InstanceId, StringComparer.Ordinal);

        foreach (var mesh in graph.Meshes)
        {
            if (!gearsById.TryGetValue(mesh.GearA, out var a) || !gearsById.TryGetValue(mesh.GearB, out var b))
            {
                continue;
            }

            var existing = sidecar.Meshes.FirstOrDefault(entry =>
                (entry.GearA == mesh.GearA && entry.GearB == mesh.GearB)
                || (entry.GearA == mesh.GearB && entry.GearB == mesh.GearA));

            Meshes.Add(new MeshRow(mesh, a, b, existing?.Decision, existing?.Reason));
        }

        foreach (var component in graph.UnsupportedComponents
            .Where(component => component.Type == MechanicalComponentType.ClutchGear)
            .OrderBy(component => component.InstanceId, StringComparer.Ordinal))
        {
            var existing = sidecar.Clutches.FirstOrDefault(entry => entry.InstanceId == component.InstanceId);
            Clutches.Add(new ClutchRow(component, existing?.State, existing?.Reason));
        }

        // A clutch already confirmed locked is no longer in the boundary list, so it has to be
        // rebuilt from the sidecar or the reviewer would lose the switch that got it there.
        foreach (var entry in sidecar.Clutches
            .Where(entry => Clutches.All(row => row.InstanceId != entry.InstanceId)))
        {
            var part = expansion.Instances
                .FirstOrDefault(instance => instance.InstanceId == entry.InstanceId)?.CanonicalPartName
                ?? "(unknown part)";

            Clutches.Add(new ClutchRow(
                new UnsupportedComponent(
                    entry.InstanceId,
                    part,
                    MechanicalComponentType.ClutchGear,
                    "Confirmed by the sidecar; shown so the decision stays reversible.",
                    null),
                entry.State,
                entry.Reason));
        }

        foreach (var driver in graph.Drivers)
        {
            var existing = sidecar.Drivers.FirstOrDefault(entry => entry.InstanceId == driver.InstanceId);
            Drivers.Add(new DriverRow(driver, existing is not null, existing?.Label, existing?.Reason));
        }

        foreach (var group in graph.UnsupportedComponents
            .Where(component => component.Type != MechanicalComponentType.ClutchGear)
            .GroupBy(component => (component.CanonicalPartName, component.Type, component.Reason))
            .OrderByDescending(group => group.Count()))
        {
            Boundaries.Add(new BoundaryRow(group.First(), group.Count()));
        }

        foreach (var stale in effect.StaleEntries)
        {
            StaleWarnings.Add($"{stale.InstanceId}: recorded {stale.Expected}, found {stale.Actual}");
        }

        ExportStatus = string.Empty;
        ExportCommand.RaiseCanExecuteChanged();
        RevertCommand.RaiseCanExecuteChanged();

        OnPropertyChanged(nameof(HasModel));
        OnPropertyChanged(nameof(HasStaleWarnings));
        OnPropertyChanged(nameof(Summary));
    }

    /// <summary>
    /// Collects the current decisions into a sidecar.
    ///
    /// Decisions the reviewer has not touched are not written, so an exported file records what
    /// somebody actually decided rather than restating every automatic result as though it had
    /// been reviewed.
    /// </summary>
    public ModelSidecar BuildSidecar()
    {
        if (_modelPath is null || _expansion is null || _graph is null)
        {
            return _sidecar;
        }

        var fingerprints = SidecarApplication.Fingerprints(_expansion);
        var referenced = new HashSet<string>(StringComparer.Ordinal);

        var meshes = ImmutableArray.CreateBuilder<MeshOverride>();
        foreach (var row in Meshes.Where(row => row.Decision is not null))
        {
            meshes.Add(new MeshOverride(row.Mesh.GearA, row.Mesh.GearB, row.Decision!.Value, row.Reason));
            referenced.Add(row.Mesh.GearA);
            referenced.Add(row.Mesh.GearB);
        }

        var clutches = ImmutableArray.CreateBuilder<ClutchOverride>();
        foreach (var row in Clutches.Where(row => row.State is not null))
        {
            clutches.Add(new ClutchOverride(row.InstanceId, row.State!.Value, row.Reason));
            referenced.Add(row.InstanceId);
        }

        var drivers = ImmutableArray.CreateBuilder<DriverDefinition>();
        foreach (var row in Drivers.Where(row => row.IsDriver))
        {
            drivers.Add(new DriverDefinition(row.InstanceId, row.Label, row.Reason));
            referenced.Add(row.InstanceId);
        }

        return _sidecar with
        {
            SchemaVersion = ModelSidecar.CurrentSchemaVersion,
            Model = Path.GetFileName(_modelPath),
            Meshes = meshes.ToImmutable(),
            Clutches = clutches.ToImmutable(),
            Drivers = drivers.ToImmutable(),
            InstanceFingerprints = fingerprints
                .Where(pair => referenced.Contains(pair.Key))
                .ToImmutableDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
        };
    }

    private void Export()
    {
        if (_modelPath is null)
        {
            return;
        }

        try
        {
            var sidecar = BuildSidecar();
            ModelSidecarIo.Save(_modelPath, sidecar);
            _sidecar = sidecar;

            var path = ModelSidecarIo.PathFor(_modelPath);
            ExportStatus = sidecar.IsEmpty
                ? $"Wrote {Path.GetFileName(path)} with no overrides; nothing has been reviewed yet."
                : $"Wrote {Path.GetFileName(path)}: {sidecar.Meshes.Length} mesh, "
                  + $"{sidecar.Clutches.Length} clutch, {sidecar.Drivers.Length} driver entries. "
                  + "Reload the model to apply them.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ExportStatus = $"Could not write the sidecar: {exception.Message}";
        }
    }

    /// <summary>Drops in-panel edits and returns to what is committed on disk.</summary>
    private void Revert()
    {
        if (_modelPath is null || _graph is null || _expansion is null)
        {
            return;
        }

        var committed = ModelSidecarIo.LoadFor(_modelPath);
        Load(_modelPath, _graph, _expansion, committed, SidecarEffect.None);
        ExportStatus = "Reverted to the committed sidecar.";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

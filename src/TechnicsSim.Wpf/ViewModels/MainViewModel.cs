using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using TechnicsSim.LDraw;
using TechnicsSim.LDraw.Colours;
using TechnicsSim.LDraw.Geometry;
using TechnicsSim.LDraw.Library;
using TechnicsSim.LDraw.Sources;
using TechnicsSim.Mechanics.Catalog;
using TechnicsSim.Mechanics.Mating;
using TechnicsSim.Mechanics.Shafts;
using TechnicsSim.Mechanics.Sidecar;
using TechnicsSim.Mechanics.Solver;
using TechnicsSim.Wpf.Rendering;

namespace TechnicsSim.Wpf.ViewModels;

/// <summary>
/// Drives the viewer. Owns loading, the model tree, and selection, and talks to the renderer
/// only through <see cref="ISceneRenderer"/> so no toolkit type reaches the UI layer.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly ISceneRenderer _renderer;
    private readonly string _repositoryRoot;

    private ILDrawFileSource? _library;
    private ILDrawFileSource? _shadow;
    private LibraryInfo? _libraryInfo;
    private ShadowLibraryInfo? _shadowInfo;
    private ColourPalette _palette = ColourPalette.Fallback;
    private RenderScene? _scene;
    private ConnectionAnalysis? _mechanics;
    private ShaftGraph? _shaftGraph;
    private ShaftSolution? _shaftSolution;
    private ShaftAnimationPlan _shaftAnimationPlan = ShaftAnimationPlan.Empty;
    private ImmutableArray<string> _mechanicalInstances = [];

    private string _status = "Ready.";
    private string _statistics = string.Empty;
    private string? _selectedInstanceId;
    private ImmutableArray<string> _selectedInstanceIds = [];
    private ModelTreeNode? _selectedNode;
    private AnimationInputChoice? _selectedAnimationInput;
    private double _animationTurns;
    private bool _isAnimationPlaying;
    private string _animationStatus = "Load a model to build and solve its drivetrain.";
    private bool _isBusy;

    public MainViewModel(ISceneRenderer renderer, string repositoryRoot)
    {
        _renderer = renderer;
        _repositoryRoot = repositoryRoot;
        Mechanics = new MechanicsPanelViewModel(HighlightFromPanel);
        Solution = new DrivetrainSolutionViewModel(HighlightFromPanel);
    }

    /// <summary>The reviewable drivetrain and its sidecar editing surface.</summary>
    public MechanicsPanelViewModel Mechanics { get; }

    /// <summary>The selected-input solution projected as shafts, edges, conflicts, and gaps.</summary>
    public DrivetrainSolutionViewModel Solution { get; }

    public ObservableCollection<ModelTreeNode> Tree { get; } = [];

    public ObservableCollection<string> AvailableModels { get; } = [];

    public ObservableCollection<AnimationInputChoice> AnimationInputs { get; } = [];

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public string Statistics
    {
        get => _statistics;
        private set => Set(ref _statistics, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => Set(ref _isBusy, value);
    }

    /// <summary>Provenance line shown in the status bar, per the plan's diagnostics requirement.</summary>
    public string LibraryDescription => _libraryInfo is null
        ? "No LDraw library found. Run scripts/bootstrap-libraries.ps1."
        : $"{_libraryInfo.UpdateTag ?? "unknown"}  |  shadow "
          + (_shadowInfo?.GitCommit is { } commit ? commit[..Math.Min(12, commit.Length)] : "unavailable");

    public string? SelectedInstanceId
    {
        get => _selectedInstanceId;
        private set
        {
            if (Set(ref _selectedInstanceId, value))
            {
                OnPropertyChanged(nameof(SelectionDetail));
                OnPropertyChanged(nameof(CanDriveSelection));
                OnPropertyChanged(nameof(SelectedDriveTarget));
            }
        }
    }

    /// <summary>What the selected instance is, including its full hierarchical ID.</summary>
    public string SelectionDetail
    {
        get
        {
            if (_selectedInstanceId is null || _scene is null)
            {
                return "Nothing selected.";
            }

            var instance = _scene.FindInstance(_selectedInstanceId);
            if (instance is null)
            {
                return _selectedInstanceId;
            }

            var detail = $"{instance.PartName}   colour {instance.Colour.Code} {instance.Colour.Value}"
                + $"{Environment.NewLine}{instance.InstanceId}";
            var shaftDetail = ShaftSelectionDetail(instance.InstanceId);
            var connections = _mechanics?.ConnectionsForInstance(instance.InstanceId).ToArray() ?? [];
            if (connections.Length == 0)
            {
                return detail + shaftDetail + $"{Environment.NewLine}No connection candidate.";
            }

            var first = connections[0];
            var selectedFeatureKey = first.InstanceA == instance.InstanceId ? first.FeatureA : first.FeatureB;
            var provenance = _mechanics?.FindFeature(selectedFeatureKey)?.Feature.Provenance;
            return detail
                + shaftDetail
                + $"{Environment.NewLine}{connections.Length:N0} candidate(s); first: {first.Kind} ({first.Confidence})"
                + $"{Environment.NewLine}{first.Rule}"
                + $"{Environment.NewLine}radial {first.Residuals.RadialLdu:F3} LDU, overlap "
                + $"{first.Residuals.AxialOverlapLdu:F3} LDU"
                + (first.IsAmbiguous ? "  AMBIGUOUS" : string.Empty)
                + (provenance is null
                    ? string.Empty
                    : $"{Environment.NewLine}{provenance.ShadowFile}:{provenance.ShadowLineNumber}  "
                      + $"({provenance.TransformChain.Length:N0} transform step(s))");
        }
    }

    public ModelTreeNode? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (!Set(ref _selectedNode, value))
            {
                return;
            }

            // Selecting in the tree drives the viewport, and vice versa, through the same ID.
            if (value?.InstanceId is { } id)
            {
                SelectedInstanceId = id;
                _selectedInstanceIds = [id];
                _renderer.Highlight([id]);
            }
        }
    }

    public bool ShowEdges
    {
        get => _renderer.ShowEdges;
        set
        {
            _renderer.ShowEdges = value;
            OnPropertyChanged();
        }
    }

    public bool ShowDiagnostics
    {
        get => _renderer.ShowDiagnostics;
        set
        {
            _renderer.ShowDiagnostics = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Fades everything the drivetrain graph does not account for.</summary>
    public bool EmphasizeMechanics
    {
        get => _renderer.EmphasizeMechanics;
        set
        {
            _renderer.EmphasizeMechanics = value;
            OnPropertyChanged();
        }
    }

    /// <summary>How solid the faded context stays. 0.2 is the "80% transparent" default.</summary>
    public double GhostOpacity
    {
        get => _renderer.GhostOpacity;
        set
        {
            _renderer.GhostOpacity = value;
            OnPropertyChanged();
        }
    }

    /// <summary>False when no drivetrain was reconstructed, so there is nothing to isolate.</summary>
    public bool HasMechanicalInstances => _mechanicalInstances.Length > 0;

    public bool HasAnimationInputs => AnimationInputs.Count > 0;

    public bool CanDriveSelection => SelectedInstanceId is not null
        && _shaftGraph?.ShaftForInstance(SelectedInstanceId) is not null;

    public string SelectedDriveTarget => SelectedInstanceId is not null
        && _shaftGraph?.ShaftForInstance(SelectedInstanceId) is { } shaft
            ? $"selected: {shaft.ShaftId}"
            : "select a gear or shaft part";

    public bool CanAnimate => _shaftSolution is { IsConsistent: true }
        && !_shaftAnimationPlan.Groups.IsEmpty;

    public AnimationInputChoice? SelectedAnimationInput
    {
        get => _selectedAnimationInput;
        set
        {
            if (!Set(ref _selectedAnimationInput, value))
            {
                return;
            }

            SolveAnimation();
        }
    }

    /// <summary>Turns made by a unit-speed input, from the initial pose.</summary>
    public double AnimationTurns
    {
        get => _animationTurns;
        set
        {
            var clamped = Math.Clamp(value, 0, 1);
            if (!Set(ref _animationTurns, clamped))
            {
                return;
            }

            ApplyAnimation();
            OnPropertyChanged(nameof(AnimationTurnsLabel));
        }
    }

    public string AnimationTurnsLabel => $"{AnimationTurns:F3} turns";

    public bool IsAnimationPlaying
    {
        get => _isAnimationPlaying;
        private set
        {
            if (Set(ref _isAnimationPlaying, value))
            {
                OnPropertyChanged(nameof(PlayPauseLabel));
            }
        }
    }

    public string PlayPauseLabel => IsAnimationPlaying ? "Pause" : "Play";

    public string AnimationStatus
    {
        get => _animationStatus;
        private set => Set(ref _animationStatus, value);
    }

    /// <summary>Locates the library and lists the models shipped with the repository.</summary>
    public void Initialize()
    {
        var located = LibraryLocator.Locate(null, _repositoryRoot);
        if (located is { } found)
        {
            _library = found.Source;
            _libraryInfo = found.Info;
            _palette = ColourPalette.Load(found.Source);
        }

        var shadowInfo = LibraryLocator.LocateShadow(null, _repositoryRoot);
        if (shadowInfo is not null)
        {
            _shadowInfo = shadowInfo;
            _shadow = new DirectoryFileSource(shadowInfo.Path, "LDCad shadow library");
        }

        OnPropertyChanged(nameof(LibraryDescription));

        var models = Path.Combine(_repositoryRoot, "Models");
        if (Directory.Exists(models))
        {
            foreach (var file in Directory.EnumerateFiles(models, "*.mpd").Order(StringComparer.Ordinal))
            {
                AvailableModels.Add(file);
            }
        }

        Status = _library is null
            ? "No LDraw library configured; models cannot be loaded."
            : $"Ready. {AvailableModels.Count} model(s) available.";
    }

    /// <summary>
    /// Loads a model. Parsing and mesh building run off the UI thread so a 3,000-instance model
    /// does not freeze the window, and the renderer is touched only back on the UI thread.
    /// </summary>
    public async Task LoadModelAsync(string path)
    {
        if (_library is null)
        {
            Status = "No LDraw library configured.";
            return;
        }

        IsBusy = true;
        Status = $"Loading {Path.GetFileName(path)}...";
        Tree.Clear();
        SelectedInstanceId = null;
        _selectedInstanceIds = [];
        _mechanicalInstances = [];
        ResetAnimation();
        OnPropertyChanged(nameof(HasMechanicalInstances));

        try
        {
            var watch = Stopwatch.StartNew();

            var (scene, model, mechanics, graph, sidecar, effect) = await Task.Run(() =>
            {
                var loaded = ModelLoader.Load(path, [_library]);
                var revision = _libraryInfo?.Sha256 ?? _libraryInfo?.UpdateTag ?? "unknown";
                var cache = new PartMeshCache(loaded.Resolver, revision);
                var renderScene = new SceneBuilder(cache, _palette).Build(loaded.Expansion);
                var analysis = _shadow is null ? null : ModelConnectionAnalyzer.Analyze(loaded, _shadow);

                // The drivetrain needs both the shadow features and the catalog. Either being
                // absent is a normal setup, not an error, so the panel simply stays empty.
                ShaftGraph? built = null;
                var loadedSidecar = ModelSidecarIo.LoadFor(path);
                var sidecarEffect = SidecarEffect.None;

                if (analysis is not null && TryLoadCatalog() is { } catalog)
                {
                    (built, sidecarEffect) = SidecarApplication.Build(
                        analysis, loaded.Expansion, catalog, loadedSidecar);
                }

                return (renderScene, loaded, analysis, built, loadedSidecar, sidecarEffect);
            });

            watch.Stop();
            _scene = scene;
            _mechanics = mechanics;

            var stats = _renderer.Load(scene);
            _renderer.SetMechanicsDiagnostics(mechanics);
            _renderer.ZoomToFit();

            Tree.Add(ModelTreeNode.Build(scene, model.Root.Name));

            if (graph is not null)
            {
                Mechanics.Load(path, graph, model.Expansion, sidecar, effect);
                _shaftGraph = graph;
                _mechanicalInstances = graph.MechanicalInstanceIds();
                Solution.Load(graph, null);
                ConfigureAnimationInputs(graph, sidecar);
            }
            else
            {
                AnimationStatus = mechanics is null
                    ? "No drivetrain graph: the LDCad shadow library is unavailable."
                    : "No drivetrain graph: the mechanics catalog could not be loaded.";
                Solution.Clear(AnimationStatus);
            }

            _renderer.SetMechanicalInstances(_mechanicalInstances);
            OnPropertyChanged(nameof(HasMechanicalInstances));
            OnPropertyChanged(nameof(CanDriveSelection));
            OnPropertyChanged(nameof(SelectedDriveTarget));

            Statistics =
                $"{stats.Instances:N0} instances   {stats.InstancedModels:N0} batches   "
                + $"{stats.DistinctVertexBuffers:N0} buffers   "
                + $"{stats.UploadedTriangles:N0} tris uploaded / {stats.DrawnTriangles:N0} drawn"
                + (mechanics is null
                    ? "   no shadow diagnostics"
                    : $"   {mechanics.Features.Length:N0} features / {mechanics.Connections.Length:N0} mates / "
                      + $"{mechanics.Ambiguities.Length:N0} ambiguous")
                + (_mechanicalInstances.Length == 0
                    ? string.Empty
                    : $"   {_mechanicalInstances.Length:N0} drivetrain parts");

            var unresolved = model.Expansion.Unresolved.Length;
            Status = unresolved == 0
                ? $"Loaded {Path.GetFileName(path)} in {watch.ElapsedMilliseconds:N0} ms."
                : $"Loaded {Path.GetFileName(path)} with {unresolved:N0} unresolved reference(s).";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        {
            Status = $"Failed to load {Path.GetFileName(path)}: {ex.Message}";
            AnimationStatus = "Drivetrain unavailable because the model failed to load.";
            Solution.Clear(AnimationStatus);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Loads the mechanics catalog, treating its absence as a missing optional input rather
    /// than a failure: the viewer still renders and the mechanics panel stays empty.
    /// </summary>
    private MechanicsCatalog? TryLoadCatalog()
    {
        try
        {
            return CatalogLocator.Load(null, _repositoryRoot);
        }
        catch (Exception exception) when (exception is FileNotFoundException or CatalogValidationException)
        {
            return null;
        }
    }

    private void ConfigureAnimationInputs(ShaftGraph graph, ModelSidecar sidecar)
    {
        AnimationInputs.Clear();

        var reviewedById = sidecar.Drivers.ToDictionary(
            driver => driver.InstanceId, StringComparer.Ordinal);
        var reviewedInputs = graph.Drivers
            .Where(driver => driver.ShaftId is not null && reviewedById.ContainsKey(driver.InstanceId))
            .Select(driver => new ShaftInput(
                driver.ShaftId!,
                ExactRatio.One,
                reviewedById[driver.InstanceId].Label))
            .ToImmutableArray();

        if (reviewedInputs.Length > 1)
        {
            AnimationInputs.Add(new AnimationInputChoice(
                $"All reviewed drivers ({reviewedInputs.Length})",
                reviewedInputs));
        }

        foreach (var driver in graph.Drivers.Where(driver => driver.ShaftId is not null))
        {
            var label = reviewedById.GetValueOrDefault(driver.InstanceId)?.Label ?? driver.Label;
            AnimationInputs.Add(new AnimationInputChoice(
                label,
                [new ShaftInput(driver.ShaftId!, ExactRatio.One, label)]));
        }

        OnPropertyChanged(nameof(HasAnimationInputs));
        var initial = AnimationInputs.FirstOrDefault();
        if (initial is null)
        {
            AnimationStatus = $"Graph loaded: {graph.Shafts.Length:N0} shafts, but no motor has a keyed output shaft. "
                + "Select a gear or shaft part and choose Drive selection.";
            SelectedAnimationInput = null;
        }
        else
        {
            SelectedAnimationInput = initial;
        }
    }

    private void SolveAnimation()
    {
        IsAnimationPlaying = false;
        _animationTurns = 0;
        _shaftAnimationPlan = ShaftAnimationPlan.Empty;
        OnPropertyChanged(nameof(AnimationTurns));
        OnPropertyChanged(nameof(AnimationTurnsLabel));
        _renderer.SetInstanceTransforms(new Dictionary<string, System.Numerics.Matrix4x4>());

        if (_shaftGraph is null || SelectedAnimationInput is null)
        {
            _shaftSolution = null;
            AnimationStatus = _shaftGraph is null
                ? "Load a model to build and solve its drivetrain."
                : "No driver selected. Select a gear or shaft part and choose Drive selection.";
            if (_shaftGraph is not null)
            {
                Solution.Load(_shaftGraph, null);
            }
        }
        else
        {
            _shaftSolution = ShaftSolver.Solve(_shaftGraph, SelectedAnimationInput.Inputs);
            _shaftAnimationPlan = _scene is null
                ? ShaftAnimationPlan.Empty
                : ShaftAnimation.CreatePlan(_scene, _shaftGraph, _shaftSolution);
            AnimationStatus = _shaftSolution.IsConsistent
                ? $"{_shaftSolution.Shafts.Length:N0} of {_shaftGraph.Shafts.Length:N0} shafts solved; "
                    + "unsupported mechanisms remain static."
                : $"Animation stopped: {_shaftSolution.Conflicts.Length:N0} exact constraint conflict(s). "
                    + _shaftSolution.Conflicts[0].Message;
            Solution.Load(_shaftGraph, _shaftSolution);
        }

        OnPropertyChanged(nameof(CanAnimate));
        OnPropertyChanged(nameof(SelectionDetail));
    }

    private void ApplyAnimation()
    {
        if (_scene is null || _shaftGraph is null || _shaftSolution is null)
        {
            return;
        }

        _renderer.SetInstanceTransforms(ShaftAnimation.BuildTransforms(
            _shaftAnimationPlan,
            AnimationTurns));
    }

    private void ResetAnimation()
    {
        IsAnimationPlaying = false;
        _shaftGraph = null;
        _shaftSolution = null;
        _shaftAnimationPlan = ShaftAnimationPlan.Empty;
        AnimationInputs.Clear();
        _selectedAnimationInput = null;
        _animationTurns = 0;
        AnimationStatus = "Load a model to build and solve its drivetrain.";
        Solution.Clear();
        _renderer.SetInstanceTransforms(new Dictionary<string, System.Numerics.Matrix4x4>());
        OnPropertyChanged(nameof(SelectedAnimationInput));
        OnPropertyChanged(nameof(AnimationTurns));
        OnPropertyChanged(nameof(AnimationTurnsLabel));
        OnPropertyChanged(nameof(HasAnimationInputs));
        OnPropertyChanged(nameof(CanAnimate));
        OnPropertyChanged(nameof(CanDriveSelection));
        OnPropertyChanged(nameof(SelectedDriveTarget));
    }

    /// <summary>Makes the shaft under the current selection a temporary unit-speed input.</summary>
    public void DriveSelection()
    {
        if (SelectedInstanceId is null
            || _shaftGraph?.ShaftForInstance(SelectedInstanceId) is not { } shaft)
        {
            return;
        }

        var label = $"Manual input: {shaft.ShaftId}";
        foreach (var prior in AnimationInputs
            .Where(candidate => candidate.DisplayName.StartsWith("Manual input:", StringComparison.Ordinal))
            .ToArray())
        {
            AnimationInputs.Remove(prior);
        }

        var choice = new AnimationInputChoice(
            label,
            [new ShaftInput(shaft.ShaftId, ExactRatio.One, label)]);
        AnimationInputs.Insert(0, choice);
        OnPropertyChanged(nameof(HasAnimationInputs));
        SelectedAnimationInput = choice;
    }

    public void ToggleAnimation()
    {
        if (CanAnimate)
        {
            IsAnimationPlaying = !IsAnimationPlaying;
        }
    }

    public void ResetAnimationPosition()
    {
        IsAnimationPlaying = false;
        AnimationTurns = 0;
        _renderer.SetInstanceTransforms(new Dictionary<string, System.Numerics.Matrix4x4>());
    }

    public void AdvanceAnimation(double elapsedSeconds)
    {
        if (!IsAnimationPlaying || elapsedSeconds <= 0)
        {
            return;
        }

        const double turnsPerSecond = 0.2;
        AnimationTurns = (AnimationTurns + (elapsedSeconds * turnsPerSecond)) % 1.0;
    }

    private string ShaftSelectionDetail(string instanceId)
    {
        var shaft = _shaftGraph?.ShaftForInstance(instanceId);
        if (shaft is null)
        {
            return string.Empty;
        }

        if (_shaftGraph!.UnsupportedComponents.Any(component => component.InstanceId == instanceId))
        {
            return $"{Environment.NewLine}{shaft.ShaftId}   unsupported boundary (static)";
        }

        var state = _shaftSolution?.Find(shaft.ShaftId);
        if (state is null)
        {
            return $"{Environment.NewLine}{shaft.ShaftId}   unsolved (static)";
        }

        var suffix = _shaftGraph.Drivers.Any(driver => driver.InstanceId == instanceId)
            ? "   motor housing static"
            : string.Empty;
        return $"{Environment.NewLine}{shaft.ShaftId}   omega {state.AngularVelocity} x input"
            + $" ({state.AngularVelocity.ToDouble():F6}){suffix}";
    }

    /// <summary>Highlights the instances chosen in the mechanics panel, and mirrors them in the tree.</summary>
    private void HighlightFromPanel(ImmutableArray<string> instanceIds)
    {
        if (instanceIds.IsEmpty)
        {
            return;
        }

        // Finding a 24-tooth gear by eye inside a 3,000-part model is hopeless, so choosing a row
        // turns the isolation on rather than leaving the reviewer to discover the checkbox.
        if (HasMechanicalInstances)
        {
            EmphasizeMechanics = true;
        }

        SelectedInstanceId = instanceIds[0];
        _selectedInstanceIds = instanceIds;
        _renderer.Highlight(instanceIds);
        SelectTreeNode(instanceIds[0]);
        _renderer.ZoomToInstances(instanceIds);
    }

    /// <summary>Handles a viewport click, resolving it to a logical instance.</summary>
    public void SelectAt(System.Windows.Point viewportPoint)
    {
        var instanceId = _renderer.PickInstance(viewportPoint);

        SelectedInstanceId = instanceId;
        _selectedInstanceIds = instanceId is null ? [] : [instanceId];
        _renderer.Highlight(_selectedInstanceIds);

        if (instanceId is not null)
        {
            SelectTreeNode(instanceId);
        }
    }

    /// <summary>Expands the tree down to the node carrying an instance ID and selects it.</summary>
    private void SelectTreeNode(string instanceId)
    {
        foreach (var root in Tree)
        {
            if (Find(root, instanceId) is { } path)
            {
                foreach (var node in path)
                {
                    node.IsExpanded = true;
                }

                _selectedNode = path[^1];
                OnPropertyChanged(nameof(SelectedNode));
                return;
            }
        }
    }

    private static List<ModelTreeNode>? Find(ModelTreeNode node, string instanceId)
    {
        if (node.InstanceId == instanceId)
        {
            return [node];
        }

        foreach (var child in node.Children)
        {
            if (Find(child, instanceId) is { } found)
            {
                found.Insert(0, node);
                return found;
            }
        }

        return null;
    }

    public void ZoomToFit() => _renderer.ZoomToFit();

    public void ZoomToSelection()
    {
        if (!_selectedInstanceIds.IsEmpty)
        {
            _renderer.ZoomToInstances(_selectedInstanceIds);
        }
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

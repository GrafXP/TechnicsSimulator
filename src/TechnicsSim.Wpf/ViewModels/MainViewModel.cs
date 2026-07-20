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
    private LibraryInfo? _libraryInfo;
    private ColourPalette _palette = ColourPalette.Fallback;
    private RenderScene? _scene;

    private string _status = "Ready.";
    private string _statistics = string.Empty;
    private string? _selectedInstanceId;
    private ModelTreeNode? _selectedNode;
    private bool _isBusy;

    public MainViewModel(ISceneRenderer renderer, string repositoryRoot)
    {
        _renderer = renderer;
        _repositoryRoot = repositoryRoot;
    }

    public ObservableCollection<ModelTreeNode> Tree { get; } = [];

    public ObservableCollection<string> AvailableModels { get; } = [];

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
        : $"{_libraryInfo.UpdateTag ?? "unknown"}  |  {Path.GetFileName(_libraryInfo.Path)}";

    public string? SelectedInstanceId
    {
        get => _selectedInstanceId;
        private set
        {
            if (Set(ref _selectedInstanceId, value))
            {
                OnPropertyChanged(nameof(SelectionDetail));
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

            return $"{instance.PartName}   colour {instance.Colour.Code} {instance.Colour.Value}"
                + $"{Environment.NewLine}{instance.InstanceId}";
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
                _renderer.Highlight(id);
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

        try
        {
            var watch = Stopwatch.StartNew();

            var (scene, model) = await Task.Run(() =>
            {
                var loaded = ModelLoader.Load(path, [_library]);
                var revision = _libraryInfo?.Sha256 ?? _libraryInfo?.UpdateTag ?? "unknown";
                var cache = new PartMeshCache(loaded.Resolver, revision);
                return (new SceneBuilder(cache, _palette).Build(loaded.Expansion), loaded);
            });

            watch.Stop();
            _scene = scene;

            var stats = _renderer.Load(scene);
            _renderer.ZoomToFit();

            Tree.Add(ModelTreeNode.Build(scene, model.Root.Name));

            Statistics =
                $"{stats.Instances:N0} instances   {stats.InstancedModels:N0} batches   "
                + $"{stats.DistinctVertexBuffers:N0} buffers   "
                + $"{stats.UploadedTriangles:N0} tris uploaded / {stats.DrawnTriangles:N0} drawn";

            var unresolved = model.Expansion.Unresolved.Length;
            Status = unresolved == 0
                ? $"Loaded {Path.GetFileName(path)} in {watch.ElapsedMilliseconds:N0} ms."
                : $"Loaded {Path.GetFileName(path)} with {unresolved:N0} unresolved reference(s).";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        {
            Status = $"Failed to load {Path.GetFileName(path)}: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Handles a viewport click, resolving it to a logical instance.</summary>
    public void SelectAt(System.Windows.Point viewportPoint)
    {
        var instanceId = _renderer.PickInstance(viewportPoint);

        SelectedInstanceId = instanceId;
        _renderer.Highlight(instanceId);

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
        if (_selectedInstanceId is { } id)
        {
            _renderer.ZoomToInstance(id);
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

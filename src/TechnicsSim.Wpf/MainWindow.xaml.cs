using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HelixToolkit.SharpDX;
using HelixToolkit.Wpf.SharpDX;
using TechnicsSim.Wpf.Rendering;
using TechnicsSim.Wpf.ViewModels;

namespace TechnicsSim.Wpf;

/// <summary>
/// The viewer shell. It wires the viewport to the renderer and forwards input to the view
/// model; all decisions live in <see cref="MainViewModel"/>.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly IEffectsManager _effectsManager = new DefaultEffectsManager();

    public MainWindow()
    {
        InitializeComponent();

        Viewport.EffectsManager = _effectsManager;

        var renderer = new HelixSceneRenderer(Viewport);
        _viewModel = new MainViewModel(renderer, FindRepositoryRoot());
        DataContext = _viewModel;

        Loaded += OnLoaded;
        Closed += (_, _) => _effectsManager.Dispose();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel.Initialize();

        var choices = _viewModel.AvailableModels
            .Select(path => new ModelChoice(Path.GetFileName(path), path))
            .ToList();

        ModelSelector.ItemsSource = choices;
        ModelSelector.DisplayMemberPath = nameof(ModelChoice.Name);

        _viewModel.ShowDiagnostics = App.StartWithDiagnostics;
        _viewModel.ShowEdges = App.StartWithEdges;

        if (App.StartupModel is { } requested)
        {
            var full = Path.GetFullPath(requested);
            var match = choices.FindIndex(
                c => string.Equals(c.Path, full, StringComparison.OrdinalIgnoreCase));

            if (match >= 0)
            {
                ModelSelector.SelectedIndex = match;
                return;
            }

            // A model outside Models/ is still loadable; it just is not in the picker.
            await _viewModel.LoadModelAsync(full);
            return;
        }

        // Load the first model straight away so the window is never an empty grey box.
        if (choices.Count > 0)
        {
            ModelSelector.SelectedIndex = 0;
        }
    }

    private sealed record ModelChoice(string Name, string Path);

    private async void OnModelSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ModelSelector.SelectedItem is ModelChoice choice)
        {
            await _viewModel.LoadModelAsync(choice.Path);
        }
    }

    private System.Windows.Point? _pressPoint;

    // Left is reserved for selection; right-drag rotates and middle-drag pans through the
    // viewport's explicit input bindings. Selection happens on release so a stray left-drag
    // does not accidentally pick a part.
    private void OnViewportPress(object sender, MouseButtonEventArgs e) =>
        _pressPoint = e.GetPosition(Viewport);

    private void OnViewportRelease(object sender, MouseButtonEventArgs e)
    {
        if (_pressPoint is not { } press)
        {
            return;
        }

        _pressPoint = null;
        var release = e.GetPosition(Viewport);
        var moved = Math.Abs(release.X - press.X) + Math.Abs(release.Y - press.Y);

        const double dragThreshold = 4.0;
        if (moved <= dragThreshold)
        {
            _viewModel.SelectAt(release);
        }
    }

    /// <summary>
    /// Selects the mechanics row that was clicked.
    ///
    /// This tunnels rather than bubbles, so the row is selected before a radio button or reason
    /// box inside it takes the click. The event is left unhandled so those controls still work.
    /// </summary>
    private void OnMechanicsRowClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: MechanicsRow row })
        {
            _viewModel.Mechanics.SelectedRow = row;
        }
    }

    private void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is ModelTreeNode node)
        {
            _viewModel.SelectedNode = node;
        }
    }

    private void OnZoomToFit(object sender, RoutedEventArgs e) => _viewModel.ZoomToFit();

    private void OnZoomToSelection(object sender, RoutedEventArgs e) => _viewModel.ZoomToSelection();

    /// <summary>
    /// Walks up from the executable to the repository so the viewer finds Models/ and Library/
    /// when run from bin/, which is how it is launched during development.
    /// </summary>
    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git"))
                || File.Exists(Path.Combine(directory.FullName, "TechnicsSimulator.slnx"))
                || File.Exists(Path.Combine(directory.FullName, "TechnicsSimulator.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return Directory.GetCurrentDirectory();
    }
}

using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using TechnicsSim.Mechanics.Catalog;
using TechnicsSim.Mechanics.Shafts;
using TechnicsSim.Mechanics.Sidecar;

namespace TechnicsSim.Wpf.ViewModels;

/// <summary>Shared change notification for the mechanics rows.</summary>
public abstract class MechanicsRow : INotifyPropertyChanged
{
    private bool _isSelected;

    /// <summary>
    /// The instances the 3D view should highlight when this row is selected.
    ///
    /// More than one because a gear mesh is a statement about a pair: highlighting one of the two
    /// gears would leave the reviewer hunting for the partner the row is actually about.
    /// </summary>
    public abstract ImmutableArray<string> HighlightInstanceIds { get; }

    /// <summary>True once the reviewer has changed something that export would record.</summary>
    public abstract bool IsReviewed { get; }

    /// <summary>
    /// Drives the card's selected styling. Owned by <see cref="MechanicsPanelViewModel"/>, which
    /// keeps it to one row across all four sections.
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
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

/// <summary>
/// One proposed gear mesh, with the reviewer's accept/reject decision.
///
/// Everything the Phase 3 gate asks a mesh to show is on the row itself: tooth counts, the exact
/// ratio, the measured and expected centre distance, face overlap, confidence, and the rule that
/// produced it. A reviewer should not have to open a JSON file to judge a mesh.
/// </summary>
public sealed class MeshRow : MechanicsRow
{
    private ReviewDecision? _decision;
    private string _reason;

    public MeshRow(GearMesh mesh, MountedGear a, MountedGear b, ReviewDecision? decision, string? reason)
    {
        Mesh = mesh;
        GearA = a;
        GearB = b;
        _decision = decision;
        _reason = reason ?? string.Empty;
    }

    public GearMesh Mesh { get; }

    public MountedGear GearA { get; }

    public MountedGear GearB { get; }

    public override ImmutableArray<string> HighlightInstanceIds => [Mesh.GearA, Mesh.GearB];

    public override bool IsReviewed => _decision is not null;

    public string Headline =>
        $"{Mesh.Kind}  {Teeth(GearA)} -> {Teeth(GearB)}   {Sign}{Mesh.RatioNumerator}:{Mesh.RatioDenominator}";

    public string Parts => $"{GearA.CanonicalPartName} [{Mesh.ShaftA}]  <->  {GearB.CanonicalPartName} [{Mesh.ShaftB}]";

    /// <summary>Measured against predicted, with "n/a" where this build makes no prediction.</summary>
    public string Metrics =>
        $"centre {Mesh.CentreDistanceLdu:F2} vs {Format(Mesh.ExpectedCentreDistanceLdu)}"
        + $"   residual {Format(Mesh.CentreResidualLdu)}"
        + $"   overlap {Mesh.FaceOverlapLdu:F2}   axes {Mesh.AxisAngleDegrees:F1} deg";

    public string Provenance =>
        $"{Mesh.Rule}   |   axes from {GearA.AxisSource} / {GearB.AxisSource}";

    public string ConfidenceLabel => Mesh.Confidence.ToString();

    public ReviewDecision? Decision
    {
        get => _decision;
        set
        {
            if (Set(ref _decision, value))
            {
                if (value is not null && _reason.Length == 0)
                {
                    Reason = value == ReviewDecision.Reject
                        ? "Reviewed in the viewer as not engaging."
                        : "Reviewed in the viewer as engaging.";
                }

                OnPropertyChanged(nameof(IsReviewed));
                OnPropertyChanged(nameof(StatusLabel));
            }
        }
    }

    public string Reason
    {
        get => _reason;
        set => Set(ref _reason, value);
    }

    public string StatusLabel => _decision switch
    {
        ReviewDecision.Accept => "accepted",
        ReviewDecision.Reject => "rejected",
        _ => "automatic",
    };

    private string Sign => Mesh.Sign < 0 ? "-" : "+";

    private static string Teeth(MountedGear gear) =>
        gear.Mechanics.Gear is { } toothed ? $"{toothed.Teeth}T"
        : gear.Mechanics.Worm is { } worm ? $"{worm.Starts}-start"
        : "-";

    private static string Format(float value) => float.IsNaN(value) ? "n/a" : value.ToString("F2");
}

/// <summary>
/// A clutch gear awaiting a locked/free decision.
///
/// Left unreviewed it stays an unsupported boundary. That is the honest default: whether the
/// teeth and the axle share an angular velocity is load-dependent, and the solver models no load.
/// </summary>
public sealed class ClutchRow : MechanicsRow
{
    private ClutchState? _state;
    private string _reason;

    public ClutchRow(UnsupportedComponent component, ClutchState? state, string? reason)
    {
        Component = component;
        _state = state;
        _reason = reason ?? string.Empty;
    }

    public UnsupportedComponent Component { get; }

    public override ImmutableArray<string> HighlightInstanceIds => [Component.InstanceId];

    public override bool IsReviewed => _state is not null;

    public string Headline => $"{Component.CanonicalPartName}   {Component.Type}";

    public string InstanceId => Component.InstanceId;

    public string BoundaryReason => Component.Reason;

    public ClutchState? State
    {
        get => _state;
        set
        {
            if (Set(ref _state, value))
            {
                if (value is not null && _reason.Length == 0)
                {
                    Reason = value == ClutchState.Locked
                        ? "Kinematic modelling choice: no load is modelled, so the clutch never "
                          + "reaches its slip threshold and behaves as a rigid coupling."
                        : "Reviewed as a deliberately disengaged path.";
                }

                OnPropertyChanged(nameof(IsReviewed));
                OnPropertyChanged(nameof(StatusLabel));
            }
        }
    }

    public string Reason
    {
        get => _reason;
        set => Set(ref _reason, value);
    }

    public string StatusLabel => _state switch
    {
        ClutchState.Locked => "locked",
        ClutchState.Free => "free",
        _ => "unreviewed boundary",
    };
}

/// <summary>A motor the reviewer can name and enable as a solver input.</summary>
public sealed class DriverRow : MechanicsRow
{
    private bool _isDriver;
    private string _label;
    private string _reason;

    public DriverRow(MountedDriver driver, bool isDriver, string? label, string? reason)
    {
        Driver = driver;
        _isDriver = isDriver;
        _label = label ?? driver.Label;
        _reason = reason ?? string.Empty;
    }

    public MountedDriver Driver { get; }

    public override ImmutableArray<string> HighlightInstanceIds => [Driver.InstanceId];

    public override bool IsReviewed => _isDriver;

    public string Headline => $"{Driver.CanonicalPartName}   {Driver.ShaftId ?? "(no keyed shaft)"}";

    public string InstanceId => Driver.InstanceId;

    public bool IsDriver
    {
        get => _isDriver;
        set
        {
            if (Set(ref _isDriver, value))
            {
                if (value && _reason.Length == 0)
                {
                    Reason = "Powered input, enabled in the viewer.";
                }

                OnPropertyChanged(nameof(IsReviewed));
            }
        }
    }

    public string Label
    {
        get => _label;
        set => Set(ref _label, value);
    }

    public string Reason
    {
        get => _reason;
        set => Set(ref _reason, value);
    }
}

/// <summary>A mechanism the graph deliberately will not solve, shown with its reason.</summary>
public sealed class BoundaryRow(UnsupportedComponent component, int count) : MechanicsRow
{
    public UnsupportedComponent Component { get; } = component;

    public int Count { get; } = count;

    public override ImmutableArray<string> HighlightInstanceIds => [Component.InstanceId];

    public override bool IsReviewed => false;

    public string Headline => $"{Count} x  {Component.CanonicalPartName}   {Component.Type}";

    public string BoundaryReason => Component.Reason;
}

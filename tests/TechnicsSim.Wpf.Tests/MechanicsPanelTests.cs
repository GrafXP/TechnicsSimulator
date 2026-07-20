using System.Collections.Immutable;
using System.Numerics;
using TechnicsSim.LDraw.Expansion;
using TechnicsSim.LDraw.Resolution;
using TechnicsSim.Mechanics.Catalog;
using TechnicsSim.Mechanics.Shafts;
using TechnicsSim.Mechanics.Sidecar;
using TechnicsSim.Wpf.ViewModels;

namespace TechnicsSim.Wpf.Tests;

/// <summary>
/// Covers the review surface: what an edit records, what it deliberately does not, and that the
/// panel writes the same sidecar shape the CLI reads.
/// </summary>
public sealed class MechanicsPanelTests
{
    [Fact]
    public void AnUntouchedGraphExportsNoOverrides()
    {
        var panel = Load(out _);

        var sidecar = panel.BuildSidecar();

        // Restating every automatic result as a reviewed decision would make the file useless
        // for telling apart what a person decided from what inference produced.
        Assert.True(sidecar.IsEmpty);
    }

    [Fact]
    public void RejectingAMeshRecordsTheDecisionAndADefaultReason()
    {
        var panel = Load(out _);

        panel.Meshes[0].Decision = ReviewDecision.Reject;

        var sidecar = panel.BuildSidecar();
        var entry = Assert.Single(sidecar.Meshes);

        Assert.Equal(ReviewDecision.Reject, entry.Decision);
        Assert.False(string.IsNullOrWhiteSpace(entry.Reason));
    }

    [Fact]
    public void AnEditedReasonSurvivesExport()
    {
        var panel = Load(out _);

        panel.Meshes[0].Decision = ReviewDecision.Reject;
        panel.Meshes[0].Reason = "shares a housing but never engages";

        Assert.Equal("shares a housing but never engages", Assert.Single(panel.BuildSidecar().Meshes).Reason);
    }

    [Fact]
    public void SettingADecisionBackToAutomaticDropsItFromTheExport()
    {
        var panel = Load(out _);

        panel.Meshes[0].Decision = ReviewDecision.Reject;
        Assert.Single(panel.BuildSidecar().Meshes);

        panel.Meshes[0].Decision = null;
        Assert.Empty(panel.BuildSidecar().Meshes);
    }

    [Fact]
    public void LockingAClutchIsRecordedWithItsReason()
    {
        var panel = Load(out _);

        var clutch = Assert.Single(panel.Clutches);
        Assert.Null(clutch.State);
        Assert.Equal("unreviewed boundary", clutch.StatusLabel);

        clutch.State = ClutchState.Locked;

        var entry = Assert.Single(panel.BuildSidecar().Clutches);
        Assert.Equal(ClutchState.Locked, entry.State);
        Assert.Contains("no load", entry.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnablingADriverRecordsItsLabel()
    {
        var panel = Load(out _);

        var driver = Assert.Single(panel.Drivers);
        driver.IsDriver = true;
        driver.Label = "PF XL Motor (left)";

        var entry = Assert.Single(panel.BuildSidecar().Drivers);
        Assert.Equal("PF XL Motor (left)", entry.Label);
        Assert.False(string.IsNullOrWhiteSpace(entry.Reason));
    }

    [Fact]
    public void ExportedOverridesCarryFingerprintsForTheInstancesTheyName()
    {
        var panel = Load(out _);

        panel.Clutches[0].State = ClutchState.Locked;

        var sidecar = panel.BuildSidecar();

        // Only referenced instances are fingerprinted, so the file stays about what was reviewed.
        Assert.True(sidecar.InstanceFingerprints.ContainsKey("clutch-1"));
        Assert.Single(sidecar.InstanceFingerprints);
    }

    [Fact]
    public void SelectingAMeshRowHighlightsBothOfItsGears()
    {
        var panel = Load(out var highlighted);

        panel.SelectedRow = panel.Meshes[0];

        // Showing only one gear would leave the reviewer hunting for the partner the row is about.
        Assert.Equal(
            [panel.Meshes[0].Mesh.GearA, panel.Meshes[0].Mesh.GearB],
            highlighted.Single());
    }

    [Fact]
    public void SelectionIsSingularAcrossTheFourSections()
    {
        var panel = Load(out _);

        panel.SelectedRow = panel.Meshes[0];
        Assert.True(panel.Meshes[0].IsSelected);

        // The sections are separate lists sharing one selection, so selecting in one has to
        // clear the other; nothing else is watching for that.
        panel.SelectedRow = panel.Drivers[0];

        Assert.False(panel.Meshes[0].IsSelected);
        Assert.True(panel.Drivers[0].IsSelected);
    }

    [Fact]
    public void ReselectingTheSameRowDoesNotRepeatTheHighlight()
    {
        var panel = Load(out var highlighted);

        panel.SelectedRow = panel.Meshes[0];
        panel.SelectedRow = panel.Meshes[0];

        // Every highlight rebuilds the solid pass, so a repeated click must not cost one.
        Assert.Single(highlighted);
    }

    [Fact]
    public void LoadingAModelDropsTheSelectionWithTheRowsItPointedAt()
    {
        var panel = Load(out _);
        var stale = panel.Meshes[0];

        panel.SelectedRow = stale;
        panel.Load("Models/fixture.mpd", BuildGraph(false).Graph, BuildGraph(false).Expansion,
            ModelSidecar.Empty("fixture.mpd"), SidecarEffect.None);

        Assert.Null(panel.SelectedRow);
        Assert.False(stale.IsSelected);
    }

    [Fact]
    public void AMeshRowShowsEverythingNeededToJudgeIt()
    {
        var panel = Load(out _);
        var row = panel.Meshes[0];

        // The Phase 3 gate, on one row: tooth counts, exact ratio, residuals, confidence, rule.
        Assert.Contains("8T", row.Headline);
        Assert.Contains("24T", row.Headline);
        Assert.Contains("1:3", row.Headline);
        Assert.Contains("residual", row.Metrics);
        Assert.Contains("ExternalSpurModuleRule", row.Provenance);
        Assert.Equal("High", row.ConfidenceLabel);
    }

    [Fact]
    public void AnUnpredictedQuantityReadsAsNotApplicableRatherThanZero()
    {
        var panel = Load(out _, wormWithoutPrediction: true);

        var worm = panel.Meshes.Single(row => row.Mesh.Kind == GearMeshKind.Worm);

        // A residual printed as 0.00 would read as a perfect fit; this build made no prediction.
        Assert.Contains("n/a", worm.Metrics);
    }

    private static MechanicsPanelViewModel Load(
        out List<ImmutableArray<string>> highlighted, bool wormWithoutPrediction = false)
    {
        var captured = new List<ImmutableArray<string>>();
        highlighted = captured;

        var panel = new MechanicsPanelViewModel(captured.Add);
        var (graph, expansion) = BuildGraph(wormWithoutPrediction);

        panel.Load("Models/fixture.mpd", graph, expansion, ModelSidecar.Empty("fixture.mpd"), SidecarEffect.None);
        return panel;
    }

    private static (ShaftGraph Graph, ModelExpansion Expansion) BuildGraph(bool includeWorm)
    {
        var spurA = Gear("gear-a", "3647.dat", 8, MechanicalComponentType.SpurGear);
        var spurB = Gear("gear-b", "3648b.dat", 24, MechanicalComponentType.SpurGear);

        var gears = new List<MountedGear> { spurA, spurB };
        var meshes = new List<GearMesh>
        {
            new("gear-a", "gear-b", "shaft-a", "shaft-b", GearMeshKind.ExternalSpur,
                1, 3, -1, 40f, 40f, 0f, 20f, 0f, ConfidenceLevel.High, "ExternalSpurModuleRule"),
        };

        if (includeWorm)
        {
            var worm = new MountedGear(
                "worm-1", "4716.dat", "shaft-w",
                new PartMechanics("4716.dat", MechanicalComponentType.WormGear, MechanicalSupport.Solved,
                    "fixture", Worm: new WormGeometry(1, WormHandedness.Right)),
                Vector3.Zero, Vector3.UnitX, 0f, 10f, "fixture");

            gears.Add(worm);
            meshes.Add(new GearMesh(
                "worm-1", "gear-b", "shaft-w", "shaft-b", GearMeshKind.Worm,
                1, 24, -1, 40f, float.NaN, float.NaN, 0f, 90f, ConfidenceLevel.Medium,
                "WormAxialAdvanceRule (no reviewed worm pitch radius)"));
        }

        var clutch = new UnsupportedComponent(
            "clutch-1", "76019.dat", MechanicalComponentType.ClutchGear,
            "Torque-limiting clutch; the coupling is load-dependent.", "shaft-b");

        var sprocket = new UnsupportedComponent(
            "sprocket-1", "57519.dat", MechanicalComponentType.Sprocket,
            "Track path animation is deferred.", null);

        var driver = new MountedDriver("motor-1", "58121.dat", "shaft-a", "PF XL Motor");

        var graph = new ShaftGraph(
            [new ShaftAssembly("shaft-a", ["gear-a"], Vector3.Zero, Vector3.UnitY, [], [])],
            [.. gears],
            [.. meshes],
            [],
            [clutch, sprocket],
            [],
            [driver]);

        var expansion = new ModelExpansion(
            Root: null!,
            Instances:
            [
                Instance("gear-a", "3647.dat"),
                Instance("gear-b", "3648b.dat"),
                Instance("clutch-1", "76019.dat"),
                Instance("sprocket-1", "57519.dat"),
                Instance("motor-1", "58121.dat"),
                Instance("worm-1", "4716.dat"),
            ],
            Unresolved: [],
            AmbiguousReferences: [],
            NonPartReferences: [],
            SubmodelReferenceCount: 0,
            ExpandedInlineGeometryLines: ImmutableDictionary<int, int>.Empty);

        return (graph, expansion);
    }

    private static MountedGear Gear(string id, string part, int teeth, MechanicalComponentType type) =>
        new(id, part, $"shaft-{id[^1]}",
            new PartMechanics(part, type, MechanicalSupport.Solved, "fixture", Gear: new GearGeometry(teeth)),
            Vector3.Zero, Vector3.UnitY, teeth * 1.25f, 4f, "keyed axle feature");

    private static LogicalPartInstance Instance(string id, string part) =>
        new(id, part, part, Matrix4x4.Identity, 16, 0, ResolutionOrigin.LibraryPart, null);
}

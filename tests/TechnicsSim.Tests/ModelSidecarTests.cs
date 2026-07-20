using System.Collections.Immutable;
using TechnicsSim.LDraw;
using TechnicsSim.Mechanics.Catalog;
using TechnicsSim.Mechanics.Mating;
using TechnicsSim.Mechanics.Shafts;
using TechnicsSim.Mechanics.Sidecar;

namespace TechnicsSim.Tests;

public sealed class ModelSidecarTests
{
    [Fact]
    public void AnEmptySidecarRoundTripsThroughJson()
    {
        var sidecar = ModelSidecar.Empty("8275-1.mpd");

        var restored = ModelSidecarIo.Parse(ModelSidecarIo.ToJson(sidecar), "8275-1.mpd");

        Assert.True(restored.IsEmpty);
        Assert.Equal("8275-1.mpd", restored.Model);
    }

    [Fact]
    public void OverridesRoundTripWithTheirReasons()
    {
        var sidecar = ModelSidecar.Empty("m.mpd") with
        {
            Meshes = [new MeshOverride("gear-a", "gear-b", ReviewDecision.Reject, "shares a housing, never engages")],
            Clutches = [new ClutchOverride("clutch-1", ClutchState.Locked, "no load modelled")],
            Drivers = [new DriverDefinition("motor-1", "PF XL Motor", "powered input")],
        };

        var restored = ModelSidecarIo.Parse(ModelSidecarIo.ToJson(sidecar), "m.mpd");

        Assert.Equal(ReviewDecision.Reject, restored.MeshDecisionFor("gear-a", "gear-b"));

        // Order must not matter: a mesh is the same mesh whichever way round it is named.
        Assert.Equal(ReviewDecision.Reject, restored.MeshDecisionFor("gear-b", "gear-a"));
        Assert.Equal(ClutchState.Locked, restored.ClutchStateFor("clutch-1"));
        Assert.Equal("powered input", restored.Drivers.Single().Reason);
    }

    [Fact]
    public void ExportIsStableSoReExportingDoesNotChurnTheDiff()
    {
        var sidecar = ModelSidecar.Empty("m.mpd") with
        {
            Clutches =
            [
                new ClutchOverride("z", ClutchState.Free, "b"),
                new ClutchOverride("a", ClutchState.Locked, "a"),
            ],
        };

        var first = ModelSidecarIo.ToJson(sidecar);
        var second = ModelSidecarIo.ToJson(ModelSidecarIo.Parse(first, "m.mpd"));

        Assert.Equal(first, second);

        // Sorted, so a reviewer adding one entry gets a one-line diff.
        Assert.Equal(["a", "z"], ModelSidecarIo.Parse(first, "m.mpd").Clutches.Select(c => c.InstanceId));
    }

    [Fact]
    public void ARewrittenSchemaVersionIsRejectedRatherThanMisread()
    {
        var json = ModelSidecarIo.ToJson(ModelSidecar.Empty("m.mpd")).Replace("\"schemaVersion\": 1", "\"schemaVersion\": 99");

        var exception = Assert.Throws<InvalidDataException>(() => ModelSidecarIo.Parse(json, "m.mpd"));
        Assert.Contains("99", exception.Message);
    }

    [Fact]
    public void AMovedPartInvalidatesItsOverridesInsteadOfSilentlyRepointing()
    {
        // Instance ids encode positions in the model tree, so editing a model can repoint an id
        // at a different part. The fingerprint is what makes that visible.
        var sidecar = ModelSidecar.Empty("m.mpd") with
        {
            InstanceFingerprints = ImmutableDictionary<string, string>.Empty
                .Add("inst-1", "3647.dat@10.0,20.0,30.0")
                .Add("inst-2", "4019.dat@0.0,0.0,0.0"),
        };

        var current = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Same id, different part: exactly the dangerous case.
            ["inst-1"] = "32270.dat@10.0,20.0,30.0",
            ["inst-2"] = "4019.dat@0.0,0.0,0.0",
        };

        var stale = ModelSidecarIo.FindStaleEntries(sidecar, current);

        var entry = Assert.Single(stale);
        Assert.Equal("inst-1", entry.InstanceId);
        Assert.Contains("3647", entry.Expected);
        Assert.Contains("32270", entry.Actual);
    }

    [Fact]
    public void ADeletedPartIsReportedRatherThanIgnored()
    {
        var sidecar = ModelSidecar.Empty("m.mpd") with
        {
            InstanceFingerprints = ImmutableDictionary<string, string>.Empty
                .Add("gone", "3647.dat@1.0,2.0,3.0"),
        };

        var stale = ModelSidecarIo.FindStaleEntries(sidecar, new Dictionary<string, string>());

        Assert.Equal("(no such instance)", Assert.Single(stale).Actual);
    }

    [ShadowFact]
    public void LockingTheClutchesTurnsBoundariesIntoMeshableGears()
    {
        var model = ModelLoader.Load(
            Path.Combine(TestEnvironment.ModelsDirectory, "8275-1.mpd"), [TestEnvironment.Library!]);
        var analysis = ModelConnectionAnalyzer.Analyze(model, TestEnvironment.Shadow!);
        var catalog = CatalogLocator.Load(null, TestEnvironment.RepositoryRoot);

        var without = ShaftGraphBuilder.Build(analysis, model.Expansion, catalog);

        var clutchIds = without.UnsupportedComponents
            .Where(component => component.Type == MechanicalComponentType.ClutchGear)
            .Select(component => component.InstanceId)
            .ToList();

        Assert.NotEmpty(clutchIds);

        var sidecar = ModelSidecar.Empty("8275-1.mpd") with
        {
            Clutches = [.. clutchIds.Select(id => new ClutchOverride(id, ClutchState.Locked, "no load modelled"))],
        };

        var (with, effect) = SidecarApplication.Build(analysis, model.Expansion, catalog, sidecar);

        // Confirmed clutches stop being boundaries and start meshing, which is the whole point
        // of being able to review one.
        Assert.Equal(clutchIds.Count, effect.LockedClutches.Length);
        Assert.DoesNotContain(
            with.UnsupportedComponents,
            component => component.Type == MechanicalComponentType.ClutchGear);
        Assert.True(with.Gears.Length > without.Gears.Length);
        Assert.True(with.Meshes.Length > without.Meshes.Length);
    }

    [ShadowFact]
    public void RejectingAMeshRemovesItAndSaysSo()
    {
        var model = ModelLoader.Load(
            Path.Combine(TestEnvironment.ModelsDirectory, "8275-1.mpd"), [TestEnvironment.Library!]);
        var analysis = ModelConnectionAnalyzer.Analyze(model, TestEnvironment.Shadow!);
        var catalog = CatalogLocator.Load(null, TestEnvironment.RepositoryRoot);

        var baseline = ShaftGraphBuilder.Build(analysis, model.Expansion, catalog);
        var target = baseline.Meshes.First();

        var sidecar = ModelSidecar.Empty("8275-1.mpd") with
        {
            Meshes = [new MeshOverride(target.GearA, target.GearB, ReviewDecision.Reject, "reviewed as not engaging")],
        };

        var (graph, effect) = SidecarApplication.Build(analysis, model.Expansion, catalog, sidecar);

        Assert.Equal(baseline.Meshes.Length - 1, graph.Meshes.Length);
        Assert.Single(effect.RejectedMeshes);
        Assert.DoesNotContain(graph.Meshes, mesh => mesh.GearA == target.GearA && mesh.GearB == target.GearB);
    }

    /// <summary>
    /// The Phase 3 gate: automatic data plus the committed sidecar must give one fixed answer.
    /// </summary>
    [ShadowFact]
    public void TheCommitted8275SidecarIsCurrentAndDeterministic()
    {
        var modelPath = Path.Combine(TestEnvironment.ModelsDirectory, "8275-1.mpd");
        var model = ModelLoader.Load(modelPath, [TestEnvironment.Library!]);
        var analysis = ModelConnectionAnalyzer.Analyze(model, TestEnvironment.Shadow!);
        var catalog = CatalogLocator.Load(null, TestEnvironment.RepositoryRoot);
        var sidecar = ModelSidecarIo.LoadFor(modelPath);

        Assert.False(sidecar.IsEmpty, "8275 should have a committed reviewed sidecar.");

        var (first, effect) = SidecarApplication.Build(analysis, model.Expansion, catalog, sidecar);
        var (second, _) = SidecarApplication.Build(analysis, model.Expansion, catalog, sidecar);

        // No stale entries means every override still points at the part it was written for.
        Assert.Empty(effect.StaleEntries);

        Assert.Equal(
            first.Meshes.Select(m => $"{m.GearA}|{m.GearB}|{m.Sign}{m.RatioNumerator}:{m.RatioDenominator}"),
            second.Meshes.Select(m => $"{m.GearA}|{m.GearB}|{m.Sign}{m.RatioNumerator}:{m.RatioDenominator}"));

        // Every reviewed decision has to explain itself, or the file is not a review.
        Assert.All(sidecar.Clutches, clutch => Assert.False(string.IsNullOrWhiteSpace(clutch.Reason)));
        Assert.All(sidecar.Drivers, driver => Assert.False(string.IsNullOrWhiteSpace(driver.Reason)));

        // 8275 has four motors and the solver must support more than one driver.
        Assert.Equal(4, sidecar.Drivers.Length);
    }
}

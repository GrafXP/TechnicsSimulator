using TechnicsSim.LDraw;
using TechnicsSim.LDraw.Ast;
using TechnicsSim.LDraw.Shadow;

namespace TechnicsSim.Tests;

/// <summary>
/// Pins the parser and expander against the independent lexical/graph audit recorded in
/// PLAN.md. These numbers describe different things and must never be conflated: physical
/// type-1 lines are not the flattened part count, and expanded logical instances stop at part
/// boundaries rather than descending into part geometry.
///
/// Skips with a clear reason when no official library is configured.
/// </summary>
public sealed class RealModelBaselineTests
{
    public static TheoryData<string, int, int, int, int> Baseline => new()
    {
        // model file, MPD sections, physical type-1 lines, logical instances, distinct parts
        //
        // Note that logical instances sit above the physical line count for 8275 (submodels are
        // referenced more than once) and below it for the 42xxx models (their MPD section lists
        // include embedded parts and primitives whose internal type-1 lines are physical lines,
        // but which logical expansion correctly stops at). Both directions are expected.
        { "8275-1.mpd", 157, 3021, 3029, 138 },
        { "42055-1.mpd", 79, 4385, 3928, 146 },
        { "42100-1.mpd", 186, 8655, 7279, 183 },
        { "42121-1.mpd", 57, 1215, 576, 107 },
    };

    [RealLibraryTheory]
    [MemberData(nameof(Baseline))]
    public void ReproducesThePlanBaselineTable(
        string fileName, int sections, int physicalLines, int logicalInstances, int distinctParts)
    {
        var model = Load(fileName);

        Assert.Equal(sections, model.Sections.Length);
        Assert.Equal(physicalLines, model.PhysicalSubfileLineCount);
        Assert.Equal(logicalInstances, model.Expansion.Instances.Length);
        Assert.Equal(distinctParts, model.Expansion.PartUsage.Count);
    }

    [RealLibraryTheory]
    [MemberData(nameof(Baseline))]
    public void ResolvesEveryReferenceWithoutCyclesOrParseIssues(
        string fileName, int sections, int physicalLines, int logicalInstances, int distinctParts)
    {
        _ = (sections, physicalLines, logicalInstances, distinctParts);
        var model = Load(fileName);

        Assert.Empty(model.Expansion.Unresolved);
        Assert.Empty(model.ParseIssues);
    }

    [RealLibraryFact]
    public void ResolvesTheEmbeddedLs70PartFromTheMpdNotTheLibrary()
    {
        // The clearest demonstration that a .dat suffix does not mean "official library part".
        var model = Load("8275-1.mpd");

        var resolved = model.Resolver.Resolve("8275 - LS70.dat");
        Assert.Equal(LDraw.Resolution.ResolutionOrigin.MpdLocal, resolved.Origin);
        Assert.Equal(LDrawOrgKind.Part, resolved.Document!.OrgKind);
        Assert.True(resolved.Document.IsUnofficial);

        Assert.Equal(1630, model.Expansion.PartUsage["8275 - ls70.dat"]);
    }

    [RealLibraryFact]
    public void CountsInlineGeometrySeparatelyFromExpandedGeometry()
    {
        // 42055 is the only supplied model carrying generated hose/spring fallback meshes. The
        // expanded figure is lower than the physical one because part of that inline geometry
        // lives in sections the root never references; counting it as drawn would overstate the
        // render load. Both numbers are pinned so either one drifting is caught.
        var model = Load("42055-1.mpd");

        Assert.Equal(7433, model.PhysicalGeometryLineCounts[5]);
        Assert.Equal(6806, model.Expansion.ExpandedConditionalLines);
    }

    [RealLibraryFact]
    public void LeavesUnreferencedInlineGeometryOutOfTheExpandedScene()
    {
        // 42100 and 42121 both hold inline geometry that is entirely unreachable from their
        // roots. The distinction matters for the renderer, so it is pinned rather than assumed.
        foreach (var fileName in new[] { "42100-1.mpd", "42121-1.mpd" })
        {
            var model = Load(fileName);

            Assert.True(
                model.PhysicalGeometryLineCounts[5] > 0,
                $"{fileName} was expected to contain physical conditional lines.");
            Assert.Equal(0, model.Expansion.ExpandedConditionalLines);
        }
    }

    /// <summary>
    /// The Phase 0 gate requires that critical drivetrain parts have effective shaft features,
    /// whether inherited or cataloged. PLAN.md notes that several have no direct shadow file.
    /// This pins which route each one actually takes, so a shadow-library update that removes
    /// inherited coverage is caught rather than discovered during Phase 3.
    /// </summary>
    [ShadowFact]
    public void CriticalDrivetrainPartsReachSnapFeaturesByInheritance()
    {
        var model = Load("8275-1.mpd");
        var probe = new ShadowCoverageProbe(model.Resolver, TestEnvironment.Shadow!);

        // 8-tooth gear, 12-tooth double bevel gear, and worm: no direct shadow file, but each
        // reaches an axle-hole primitive that carries one.
        foreach (var part in new[] { "3647.dat", "32270.dat", "4716.dat" })
        {
            var coverage = probe.Probe(part);
            Assert.Equal(ShadowCoverage.Inherited, coverage.Coverage);
            Assert.Equal(0, coverage.DirectFeatureCount);
            Assert.True(coverage.InheritedFeatureCount > 0, $"{part} inherited no features.");
        }

        // The Power Functions Medium Motor reaches features by inheritance rather than a direct
        // shadow file. Those features locate the housing, not the output shaft's rotation axis
        // or direction, so Phase 3 still has to supply motor semantics from the catalog.
        var motor = probe.Probe("58120.dat");
        Assert.Equal(ShadowCoverage.Inherited, motor.Coverage);
        Assert.Equal(7, motor.InheritedFeatureCount);

        // The infra-red receiver lens and switch have no shadow data by either route. They are
        // not drivetrain parts, but they are the model's only fully uncovered parts, so pinning
        // them keeps the "uncovered" set honest instead of quietly growing.
        foreach (var receiverPart in new[] { "58149.dat", "58150.dat" })
        {
            Assert.Equal(ShadowCoverage.None, probe.Probe(receiverPart).Coverage);
        }
    }

    [ShadowTheory]
    [MemberData(nameof(Baseline))]
    public void EveryPartUsedByTheModelsIsProbableWithoutUnresolvedGeometry(
        string fileName, int sections, int physicalLines, int logicalInstances, int distinctParts)
    {
        _ = (sections, physicalLines, logicalInstances, distinctParts);
        var model = Load(fileName);
        var probe = new ShadowCoverageProbe(model.Resolver, TestEnvironment.Shadow!);

        var broken = model.Expansion.PartUsage.Keys
            .Where(part => probe.Probe(part).HasUnresolvedGeometry)
            .ToList();

        Assert.Empty(broken);
    }

    private static LoadedModel Load(string fileName) => ModelLoader.Load(
        Path.Combine(TestEnvironment.ModelsDirectory, fileName),
        [TestEnvironment.Library!]);
}

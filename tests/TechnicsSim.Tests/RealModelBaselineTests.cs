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
        { "8275-1.mpd", 157, 3021, 3029, 138 },
        { "8458 - Silver Truck (B).mpd", 29, 2093, 2068, 116 },
        { "8458 - Street Sensation (Web).mpd", 50, 2289, 2240, 117 },
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
        // PLAN.md records 11,576 physical conditional lines for the Silver model. The expanded
        // figure is higher because a spring submodel is referenced more than once.
        var model = Load("8458 - Silver Truck (B).mpd");

        Assert.Equal(11576, model.PhysicalGeometryLineCounts[5]);
        Assert.True(model.Expansion.ExpandedConditionalLines >= 11576);
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

        // 24-tooth gear, 12-tooth double bevel gear, and worm: no direct shadow file, but each
        // reaches an axle-hole primitive that carries one.
        foreach (var part in new[] { "3647.dat", "32270.dat", "4716.dat" })
        {
            var coverage = probe.Probe(part);
            Assert.Equal(ShadowCoverage.Inherited, coverage.Coverage);
            Assert.Equal(0, coverage.DirectFeatureCount);
            Assert.True(coverage.InheritedFeatureCount > 0, $"{part} inherited no features.");
        }

        // The Power Functions motor housings have no shadow data by either route. That is the
        // gap the Phase 3 mechanics catalog exists to fill, and it should stay visible.
        foreach (var motorPart in new[] { "58149.dat", "58150.dat" })
        {
            Assert.Equal(ShadowCoverage.None, probe.Probe(motorPart).Coverage);
        }
    }

    [ShadowFact]
    public void EveryPartUsedByTheModelsIsProbableWithoutUnresolvedGeometry()
    {
        var model = Load("8275-1.mpd");
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

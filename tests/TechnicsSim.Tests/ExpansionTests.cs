using System.Numerics;
using TechnicsSim.LDraw.Expansion;
using TechnicsSim.LDraw.Parsing;
using TechnicsSim.LDraw.Resolution;
using TechnicsSim.LDraw.Sources;

namespace TechnicsSim.Tests;

public sealed class ExpansionTests
{
    private static InMemoryFileSource StubLibrary() => new InMemoryFileSource("stub library")
        .Add("parts/3001.dat", "0 Brick 2 x 4\n0 !LDRAW_ORG Part UPDATE 2020-01")
        .Add("parts/3002.dat", "0 Brick 2 x 3\n0 !LDRAW_ORG Part UPDATE 2020-01")
        .Add("parts/s/1234s01.dat", "0 ~Some Subpart\n0 !LDRAW_ORG Subpart");

    private static ModelExpansion Expand(string mpdText)
    {
        var parsed = LDrawParser.Parse(mpdText, "test.mpd");
        var resolver = new LDrawResolver(parsed.Documents, [StubLibrary()]);
        return new LogicalPartExpander(resolver).Expand(parsed.Root);
    }

    [Fact]
    public void CountsLogicalInstancesNotPhysicalLines()
    {
        // One submodel defined once, referenced twice, holding two parts.
        var expansion = Expand("""
            0 FILE main.ldr
            0 !LDRAW_ORG Model
            1 16 0 0 0 1 0 0 0 1 0 0 0 1 sub.ldr
            1 16 100 0 0 1 0 0 0 1 0 0 0 1 sub.ldr

            0 FILE sub.ldr
            0 !LDRAW_ORG Model
            1 16 0 0 0 1 0 0 0 1 0 0 0 1 3001.dat
            1 16 0 20 0 1 0 0 0 1 0 0 0 1 3002.dat
            """);

        Assert.Equal(4, expansion.Instances.Length);
        Assert.Equal(2, expansion.PartUsage.Count);
        Assert.Equal(2, expansion.SubmodelReferenceCount);
        Assert.Empty(expansion.Unresolved);
    }

    [Fact]
    public void GivesEveryInstanceAStableHierarchicalId()
    {
        var expansion = Expand("""
            0 FILE main.ldr
            0 !LDRAW_ORG Model
            1 16 0 0 0 1 0 0 0 1 0 0 0 1 sub.ldr
            1 16 100 0 0 1 0 0 0 1 0 0 0 1 sub.ldr

            0 FILE sub.ldr
            0 !LDRAW_ORG Model
            1 16 0 0 0 1 0 0 0 1 0 0 0 1 3001.dat
            """);

        var ids = expansion.Instances.Select(i => i.InstanceId).ToArray();

        // The two instances come from the same source line in sub.ldr but differ by the
        // reference occurrence in main.ldr that reached them. Line numbers are file-global,
        // not section-relative, so they point straight at a line in the .mpd.
        Assert.Equal(["main.ldr@3|sub.ldr@8", "main.ldr@4|sub.ldr@8"], ids);
        Assert.Equal(ids.Length, ids.Distinct().Count());
    }

    [Fact]
    public void AccumulatesTransformsThroughSubmodels()
    {
        var expansion = Expand("""
            0 FILE main.ldr
            0 !LDRAW_ORG Model
            1 16 100 0 0 1 0 0 0 1 0 0 0 1 sub.ldr

            0 FILE sub.ldr
            0 !LDRAW_ORG Model
            1 16 10 20 30 1 0 0 0 1 0 0 0 1 3001.dat
            """);

        var instance = Assert.Single(expansion.Instances);
        Assert.Equal(new Vector3(110, 20, 30), Vector3.Transform(Vector3.Zero, instance.Transform));
    }

    [Fact]
    public void ResolvesInheritedColourThroughTheReferenceChain()
    {
        var expansion = Expand("""
            0 FILE main.ldr
            0 !LDRAW_ORG Model
            1 4 0 0 0 1 0 0 0 1 0 0 0 1 sub.ldr
            1 16 0 0 0 1 0 0 0 1 0 0 0 1 sub.ldr

            0 FILE sub.ldr
            0 !LDRAW_ORG Model
            1 16 0 0 0 1 0 0 0 1 0 0 0 1 3001.dat
            1 2 0 0 0 1 0 0 0 1 0 0 0 1 3002.dat
            """);

        // Colour 16 inherits from the referencing context; an explicit colour overrides it.
        Assert.Equal(4, expansion.Instances[0].Colour);
        Assert.Equal(2, expansion.Instances[1].Colour);

        // The second branch inherits 16 all the way down, so it stays unresolved-inherited.
        Assert.Equal(16, expansion.Instances[2].Colour);
        Assert.Equal(2, expansion.Instances[3].Colour);
    }

    [Fact]
    public void DetectsCyclesWithoutRecursingForever()
    {
        var expansion = Expand("""
            0 FILE main.ldr
            0 !LDRAW_ORG Model
            1 16 0 0 0 1 0 0 0 1 0 0 0 1 a.ldr

            0 FILE a.ldr
            0 !LDRAW_ORG Model
            1 16 0 0 0 1 0 0 0 1 0 0 0 1 b.ldr

            0 FILE b.ldr
            0 !LDRAW_ORG Model
            1 16 0 0 0 1 0 0 0 1 0 0 0 1 a.ldr
            """);

        var cycle = Assert.Single(expansion.Unresolved);
        Assert.Equal(ResolutionFailure.Cyclic, cycle.Failure);

        // The chain has to name the whole path so the offending reference is findable.
        Assert.Contains("a.ldr", cycle.ReferenceChain);
        Assert.Contains("b.ldr", cycle.ReferenceChain);
    }

    [Fact]
    public void TreatsASubpartPlacedByAModelAsAnInstanceAndFlagsIt()
    {
        // 8275 positions the Power Functions ribbon-cable end `s\58124s03.dat` directly. It
        // has its own pose and is not geometry belonging to any enclosing part, so it counts
        // as a logical instance -- but the unusual classification stays visible.
        var expansion = Expand("""
            0 FILE main.ldr
            0 !LDRAW_ORG Model
            1 16 0 0 0 1 0 0 0 1 0 0 0 1 3001.dat
            1 16 0 10 0 1 0 0 0 1 0 0 0 1 s\1234s01.dat
            """);

        Assert.Equal(2, expansion.Instances.Length);

        var flagged = Assert.Single(expansion.NonPartReferences);
        Assert.Equal(LogicalKind.Subpart, flagged.Kind);
        Assert.Equal("s\\1234s01.dat", flagged.RequestedName);
        Assert.Equal(4, flagged.LineNumber);
    }

    [Fact]
    public void SeparatesPhysicalFromExpandedInlineGeometry()
    {
        // A submodel's inline geometry is counted once per traversal, mirroring how logical
        // instances exceed physical type-1 lines.
        var expansion = Expand("""
            0 FILE main.ldr
            0 !LDRAW_ORG Model
            1 16 0 0 0 1 0 0 0 1 0 0 0 1 hose.ldr
            1 16 100 0 0 1 0 0 0 1 0 0 0 1 hose.ldr

            0 FILE hose.ldr
            0 !LDRAW_ORG Model
            4 16 0 0 0 1 0 0 1 1 0 0 1 0
            5 24 0 0 0 1 0 0 0 1 0 0 0 1
            """);

        Assert.Equal(2, expansion.ExpandedInlineGeometryLines[4]);
        Assert.Equal(2, expansion.ExpandedConditionalLines);
        Assert.Equal(4, expansion.TotalExpandedInlineGeometryLines);
    }
}

using System.Numerics;
using TechnicsSim.LDraw.Parsing;
using TechnicsSim.LDraw.Resolution;
using TechnicsSim.LDraw.Sources;
using TechnicsSim.Mechanics.Features;

namespace TechnicsSim.Tests;

public sealed class EffectiveFeatureExtractionTests
{
    private static LDrawResolver Resolver(InMemoryFileSource official) => new([], [official]);

    [Fact]
    public void PreservesCompleteCylinderProfilesAndProvenance()
    {
        var official = new InMemoryFileSource("official")
            .Add("parts/profile.dat", "0 Profile\n0 !LDRAW_ORG Part");
        var shadow = new InMemoryFileSource("shadow")
            .Add("parts/profile.dat", "0 !LDCAD SNAP_CYL [gender=F] [caps=none] [secs=R 8 2 _L 8.5 1 A 6 16 L_ 6.5 1]");

        var result = new EffectiveFeatureExtractor(Resolver(official), shadow).Extract("profile.dat");

        var feature = Assert.Single(result.Features);
        var cylinder = Assert.IsType<CylinderSnapShape>(feature.Shape);
        Assert.Equal(
            [CylinderSectionKind.Round, CylinderSectionKind.FlexiblePrevious, CylinderSectionKind.Axle, CylinderSectionKind.FlexibleNext],
            cylinder.Sections.Select(section => section.Kind));
        Assert.Equal(20f, cylinder.Length);
        Assert.Equal("parts/profile.dat", feature.Provenance.ShadowFile);
        Assert.Equal(1, feature.Provenance.ShadowLineNumber);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void ParsesClipFingerAndGenericShapesIntoTypedFiniteFeatures()
    {
        var official = new InMemoryFileSource("official")
            .Add("parts/typed.dat", "0 Typed\n0 !LDRAW_ORG Part");
        var shadow = new InMemoryFileSource("shadow")
            .Add("parts/typed.dat", """
                0 !LDCAD SNAP_CLP [radius=4] [length=8] [center=true]
                0 !LDCAD SNAP_FGR [group=hinge] [genderOfs=F] [seq=4.5 8 4.5] [radius=6] [center=true]
                0 !LDCAD SNAP_GEN [group=plug] [gender=M] [bounding=box 12.5 16.5 8]
                """);

        var features = new EffectiveFeatureExtractor(Resolver(official), shadow).Extract("typed.dat").Features;

        Assert.Equal(3, features.Length);
        Assert.Contains(features, feature => feature.Shape is ClipSnapShape { Radius: 4f, Length: 8f, Centered: true });
        Assert.Contains(features, feature => feature.Shape is FingerSnapShape { FirstGender: SnapGender.Female, Radius: 6f });
        Assert.Contains(features, feature => feature.Shape is GenericSnapShape
        {
            Gender: SnapGender.Male,
            BoundsKind: GenericBoundsKind.Box,
        });
    }

    [Fact]
    public void ExpandsCenteredAndUncenteredGridsInTheOrientedLocalPlane()
    {
        var official = new InMemoryFileSource("official")
            .Add("parts/grid.dat", "0 Grid\n0 !LDRAW_ORG Part");
        var shadow = new InMemoryFileSource("shadow")
            .Add("parts/grid.dat", """
                0 !LDCAD SNAP_CYL [gender=M] [secs=R 6 4] [grid=C 3 1 20 0]
                0 !LDCAD SNAP_CYL [gender=M] [secs=R 6 4] [pos=100 0 0] [grid=2 1 20 0]
                """);

        var features = new EffectiveFeatureExtractor(Resolver(official), shadow).Extract("grid.dat").Features;
        var x = features.Select(feature => feature.Transform.M41).Order().ToArray();

        Assert.Equal([-20f, 0f, 20f, 100f, 120f], x);
    }

    [Fact]
    public void ExpandsProductionThreeAxisGridVariant()
    {
        var official = new InMemoryFileSource("official")
            .Add("parts/grid3.dat", "0 Grid\n0 !LDRAW_ORG Part");
        var shadow = new InMemoryFileSource("shadow")
            .Add("parts/grid3.dat", "0 !LDCAD SNAP_CYL [gender=F] [secs=R 6 4] [grid=2 C 2 1 20 50 0]");

        var result = new EffectiveFeatureExtractor(Resolver(official), shadow).Extract("grid3.dat");

        Assert.Empty(result.Issues);
        Assert.Equal(4, result.Features.Length);
        Assert.Equal([-25f, 25f], result.Features.Select(feature => feature.Transform.M42).Distinct().Order());
        Assert.Equal([0f, 20f], result.Features.Select(feature => feature.Transform.M41).Distinct().Order());
    }

    [Fact]
    public void SnapClearRemovesOnlyMatchingInheritedIds()
    {
        var official = new InMemoryFileSource("official")
            .Add("p/child.dat", "0 Child\n0 !LDRAW_ORG Primitive")
            .Add("parts/parent.dat", """
                0 Parent
                0 !LDRAW_ORG Part
                1 16 0 0 0 1 0 0 0 1 0 0 0 1 child.dat
                """);
        var shadow = new InMemoryFileSource("shadow")
            .Add("p/child.dat", """
                0 !LDCAD SNAP_CYL [id=drop] [gender=M] [secs=R 6 4]
                0 !LDCAD SNAP_CYL [id=keep] [gender=M] [secs=R 6 4] [pos=20 0 0]
                """)
            .Add("parts/parent.dat", """
                0 !LDCAD SNAP_CLEAR [id=drop]
                0 !LDCAD SNAP_CYL [id=direct] [gender=F] [secs=R 6 4]
                """);

        var result = new EffectiveFeatureExtractor(Resolver(official), shadow).Extract("parent.dat");

        Assert.Equal(["direct", "keep"], result.Features.Select(feature => feature.Id).Order());
        Assert.Equal(FeatureOrigin.Inherited, result.Features.Single(feature => feature.Id == "keep").Origin);
        Assert.Equal(FeatureOrigin.Direct, result.Features.Single(feature => feature.Id == "direct").Origin);
    }

    [Fact]
    public void EmptySnapClearFlushesAllInheritedButNotFollowingDirectFeatures()
    {
        var official = new InMemoryFileSource("official")
            .Add("p/child.dat", "0 Child\n0 !LDRAW_ORG Primitive")
            .Add("parts/parent.dat", "0 Parent\n0 !LDRAW_ORG Part\n1 16 0 0 0 1 0 0 0 1 0 0 0 1 child.dat");
        var shadow = new InMemoryFileSource("shadow")
            .Add("p/child.dat", "0 !LDCAD SNAP_CYL [gender=M] [secs=R 6 4]")
            .Add("parts/parent.dat", "0 !LDCAD SNAP_CLEAR\n0 !LDCAD SNAP_CYL [gender=F] [secs=R 6 4]");

        var feature = Assert.Single(new EffectiveFeatureExtractor(Resolver(official), shadow).Extract("parent.dat").Features);

        Assert.Equal(FeatureOrigin.Direct, feature.Origin);
        Assert.Equal(SnapGender.Female, Assert.IsType<CylinderSnapShape>(feature.Shape).Gender);
    }

    [Fact]
    public void SnapIncludeIsNonRecursiveAndSupportsGridsAndIdOverride()
    {
        var official = new InMemoryFileSource("official")
            .Add("parts/parent.dat", "0 Parent\n0 !LDRAW_ORG Part");
        var shadow = new InMemoryFileSource("shadow")
            .Add("parts/parent.dat", "0 !LDCAD SNAP_INCL [id=included] [ref=template.dat] [pos=10 0 0] [grid=C 2 1 20 0]")
            .Add("parts/template.dat", """
                0 !LDCAD SNAP_CYL [id=source] [gender=F] [secs=A 6 4]
                0 !LDCAD SNAP_INCL [ref=nested.dat]
                """)
            .Add("parts/nested.dat", "0 !LDCAD SNAP_CYL [gender=M] [secs=R 6 4]");

        var result = new EffectiveFeatureExtractor(Resolver(official), shadow).Extract("parent.dat");

        Assert.Equal(2, result.Features.Length);
        Assert.All(result.Features, feature => Assert.Equal("included", feature.Id));
        Assert.All(result.Features, feature => Assert.Equal(FeatureOrigin.Included, feature.Origin));
        Assert.Equal([0f, 20f], result.Features.Select(feature => feature.Transform.M41).Order());
    }

    [Fact]
    public void SnapIncludeAppliesVectorScaleBeforeOrientationAndPosition()
    {
        var official = new InMemoryFileSource("official")
            .Add("parts/parent.dat", "0 Parent\n0 !LDRAW_ORG Part");
        var shadow = new InMemoryFileSource("shadow")
            .Add("parts/parent.dat", "0 !LDCAD SNAP_INCL [ref=template.dat] [pos=10 0 0] [scale=2 1 1] [ori=0 -1 0 1 0 0 0 0 1] [grid=2 1 10 0]")
            .Add("parts/template.dat", "0 !LDCAD SNAP_CYL [gender=M] [secs=R 2 4] [pos=1 0 0]");

        var features = new EffectiveFeatureExtractor(Resolver(official), shadow).Extract("parent.dat").Features;

        // Source x=1 is doubled, then the unscaled 10-LDU grid spacing is applied, then both
        // placements rotate onto +Y and translate. Scaling must not turn the spacing into 20.
        Assert.Equal(2, features.Length);
        Assert.All(features, feature => Assert.Equal(10f, feature.Transform.M41, 3));
        Assert.Equal([2f, 12f], features.Select(feature => feature.Transform.M42).Order());
        Assert.All(features, feature => Assert.Equal(0f, feature.Transform.M43, 3));
    }

    [Fact]
    public void ScaleAndMirrorPoliciesAreAppliedPerOfficialReference()
    {
        var official = new InMemoryFileSource("official")
            .Add("p/child.dat", "0 Child\n0 !LDRAW_ORG Primitive")
            .Add("parts/y-scale.dat", "0 Y\n0 !LDRAW_ORG Part\n1 16 0 0 0 1 0 0 0 2 0 0 0 1 child.dat")
            .Add("parts/x-scale.dat", "0 X\n0 !LDRAW_ORG Part\n1 16 0 0 0 2 0 0 0 1 0 0 0 1 child.dat")
            .Add("parts/mirror.dat", "0 M\n0 !LDRAW_ORG Part\n1 16 0 0 0 -1 0 0 0 1 0 0 0 1 child.dat");
        var shadow = new InMemoryFileSource("shadow")
            .Add("p/child.dat", "0 !LDCAD SNAP_CYL [gender=M] [secs=A 6 4] [scale=YOnly] [mirror=none]");
        var extractor = new EffectiveFeatureExtractor(Resolver(official), shadow);

        Assert.Single(extractor.Extract("y-scale.dat").Features);
        Assert.Empty(extractor.Extract("x-scale.dat").Features);
        Assert.Empty(extractor.Extract("mirror.dat").Features);
        Assert.Contains("Scale policy", Assert.Single(extractor.Extract("x-scale.dat").RejectedInheritance).Reason);
        Assert.Contains("Mirror policy", Assert.Single(extractor.Extract("mirror.dat").RejectedInheritance).Reason);
    }

    [Fact]
    public void DefaultCylinderMirrorPolicyCorrectsAndKeepsReflectedFeatures()
    {
        var official = new InMemoryFileSource("official")
            .Add("p/child.dat", "0 Child\n0 !LDRAW_ORG Primitive")
            .Add("parts/parent.dat", "0 Parent\n0 !LDRAW_ORG Part\n1 16 0 0 0 -1 0 0 0 1 0 0 0 1 child.dat");
        var shadow = new InMemoryFileSource("shadow")
            .Add("p/child.dat", "0 !LDCAD SNAP_CYL [gender=M] [secs=A 6 4]");

        var result = new EffectiveFeatureExtractor(Resolver(official), shadow).Extract("parent.dat");

        Assert.Single(result.Features);
        Assert.True(result.Features[0].Transform.GetDeterminant() < 0f);
        Assert.Empty(result.RejectedInheritance);
    }
}

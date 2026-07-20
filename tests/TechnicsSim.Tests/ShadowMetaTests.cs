using TechnicsSim.LDraw.Parsing;
using TechnicsSim.LDraw.Shadow;

namespace TechnicsSim.Tests;

public sealed class ShadowMetaTests
{
    private static IReadOnlyList<ShadowMeta> Parse(string text) =>
        ShadowMetaParser.Extract(LDrawParser.Parse(text, "shadow.dat").Root, "shadow.dat");

    [Fact]
    public void ExtractsMetaNamesAndFields()
    {
        var metas = Parse(
            "0 !LDCAD SNAP_CYL [gender=M] [caps=none] [secs=A 6 80] [center=true] "
            + "[ori=0 -1 0 1 0 0 0 0 1]");

        var meta = Assert.Single(metas);
        Assert.Equal("SNAP_CYL", meta.Name);
        Assert.True(meta.IsSnapFeature);
        Assert.Equal("M", meta.Field("gender"));
        Assert.Equal("none", meta.Field("caps"));
        Assert.Equal("true", meta.Field("center"));
        Assert.Null(meta.Field("grid"));
    }

    [Fact]
    public void KeepsWhitespaceBearingValuesIntact()
    {
        // A section profile is a whole list. Splitting it on spaces would silently truncate
        // the shape into its first token.
        var meta = Assert.Single(Parse("0 !LDCAD SNAP_CYL [secs=R 8 2   R 6 16   R 8 2]"));

        Assert.Equal("R 8 2   R 6 16   R 8 2", meta.Field("secs"));
    }

    [Fact]
    public void ReadsFieldsCaseInsensitivelyButKeepsValueCase()
    {
        var meta = Assert.Single(Parse("0 !LDCAD SNAP_CYL [Gender=M]"));

        Assert.Equal("M", meta.Field("gender"));
        Assert.Equal("M", meta.Field("GENDER"));
    }

    [Fact]
    public void ParsesAValuelessSnapClear()
    {
        var metas = Parse("""
            0 !LDCAD SNAP_CLEAR
            0 !LDCAD SNAP_CLEAR [id=someId]
            """);

        Assert.Equal(2, metas.Count);
        Assert.All(metas, m => Assert.Equal("SNAP_CLEAR", m.Name));
        Assert.False(metas[0].IsSnapFeature);
        Assert.Empty(metas[0].Fields);
        Assert.Equal("someId", metas[1].Field("id"));
    }

    [Fact]
    public void DistinguishesFeatureMetasFromOtherLDCadMetas()
    {
        var metas = Parse("""
            0 !LDCAD SNAP_CYL [gender=M]
            0 !LDCAD SNAP_CLP [radius=4]
            0 !LDCAD SNAP_FGR [genderOfs=M]
            0 !LDCAD SNAP_GEN [bounding=box 4 4 4]
            0 !LDCAD SNAP_INCL [ref=connhole.dat]
            0 !LDCAD MIRROR_INFO [type=none]
            0 !LDCAD HINTS [flags=hasStuds]
            """);

        Assert.Equal(7, metas.Count);
        Assert.Equal(4, metas.Count(m => m.IsSnapFeature));
        Assert.False(metas.Single(m => m.Name == "SNAP_INCL").IsSnapFeature);
        Assert.False(metas.Single(m => m.Name == "MIRROR_INFO").IsSnapFeature);
    }

    [Fact]
    public void RecordsSourceLinesForProvenance()
    {
        var metas = Parse("""
            0 Some shadow file header
            0 !LDCAD SNAP_CYL [gender=M]
            """);

        Assert.Equal(2, Assert.Single(metas).LineNumber);
        Assert.Equal("shadow.dat", metas[0].SourceFile);
    }

    [Fact]
    public void IgnoresNonLDCadMetas()
    {
        var metas = Parse("""
            0 BFC CERTIFY CW
            0 !HISTORY 2025-12-05 {Roland Melkert} Initial info
            0 !LDCAD SNAP_CYL [gender=M]
            """);

        Assert.Single(metas);
    }
}

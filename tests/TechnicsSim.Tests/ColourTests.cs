using TechnicsSim.LDraw.Colours;

namespace TechnicsSim.Tests;

public sealed class ColourTests
{
    private const string SampleConfig = """
        0 LDraw.org Configuration File
        0 !LDRAW_ORG Configuration UPDATE 2025-08-04
        0 !COLOUR Black    CODE 0  VALUE #1B2A34 EDGE #808080
        0 !COLOUR Red      CODE 4  VALUE #B40000 EDGE #333333
        0 !COLOUR Trans_Red CODE 36 VALUE #C91A09 EDGE #333333 ALPHA 128
        0 !COLOUR Glow     CODE 21 VALUE #E0FFB0 EDGE #B8FF4D ALPHA 240 LUMINANCE 15
        0 !COLOUR Glitter  CODE 114 VALUE #DF6695 EDGE #B9275F ALPHA 128 MATERIAL GLITTER VALUE #B9275F FRACTION 0.17
        """;

    [Fact]
    public void ParsesKeywordDrivenColourEntries()
    {
        var palette = ColourPalette.Parse(SampleConfig);

        Assert.True(palette.TryGet(4, out var red));
        Assert.Equal("Red", red.Name);
        Assert.Equal(new Rgba(0xB4, 0x00, 0x00), red.Value);
        Assert.Equal(new Rgba(0x33, 0x33, 0x33), red.Edge);
        Assert.False(red.IsTranslucent);
    }

    [Fact]
    public void AppliesAlphaAndLuminance()
    {
        var palette = ColourPalette.Parse(SampleConfig);

        Assert.True(palette.TryGet(36, out var transRed));
        Assert.Equal(128, transRed.Value.A);
        Assert.True(transRed.IsTranslucent);

        Assert.True(palette.TryGet(21, out var glow));
        Assert.Equal(15, glow.Luminance);
    }

    [Fact]
    public void DoesNotLetAMaterialClauseOverwriteTheSurfaceColour()
    {
        // A MATERIAL clause carries its own nested VALUE. Naively scanning for the last VALUE
        // would silently replace the colour of every glitter and speckle entry.
        var palette = ColourPalette.Parse(SampleConfig);

        Assert.True(palette.TryGet(114, out var glitter));
        Assert.Equal(new Rgba(0xDF, 0x66, 0x95, 128), glitter.Value);
        Assert.Equal("GLITTER", glitter.Material);
    }

    [Fact]
    public void ResolvesInheritedSurfaceColourFromTheCallingContext()
    {
        var palette = ColourPalette.Parse(SampleConfig);
        var context = palette.Resolve(4, palette.DefaultContext);

        var inherited = palette.Resolve(ColourPalette.InheritedSurfaceCode, context);

        Assert.Equal(context, inherited);
        Assert.Equal(new Rgba(0xB4, 0x00, 0x00), inherited.Value);
    }

    [Fact]
    public void ResolvesEdgeColour24FromTheCallingContextsEdge()
    {
        var palette = ColourPalette.Parse(SampleConfig);
        var context = palette.Resolve(0, palette.DefaultContext);

        var edge = palette.Resolve(ColourPalette.InheritedEdgeCode, context);

        // Black's edge is #808080, and that becomes the drawn colour of a code-24 line.
        Assert.Equal(new Rgba(0x80, 0x80, 0x80), edge.Value);

        // Nesting another inherit must not drift away from that colour.
        Assert.Equal(edge.Value, palette.Resolve(ColourPalette.InheritedEdgeCode, edge).Value);
    }

    [Theory]
    [InlineData(0x2FF0000, 0xFF, 0x00, 0x00)]
    [InlineData(0x200FF00, 0x00, 0xFF, 0x00)]
    [InlineData(0x2123456, 0x12, 0x34, 0x56)]
    public void DecodesDirectColours(int code, byte r, byte g, byte b)
    {
        Assert.True(ColourPalette.IsDirectColour(code));

        var resolved = ColourPalette.Parse(SampleConfig).Resolve(code, ColourPalette.Fallback.DefaultContext);

        Assert.Equal(new Rgba(r, g, b), resolved.Value);
    }

    [Fact]
    public void DoesNotMistakeOrdinaryCodesForDirectColours()
    {
        foreach (var code in new[] { 0, 4, 16, 24, 36, 272, 512 })
        {
            Assert.False(ColourPalette.IsDirectColour(code), $"Code {code} was read as a direct colour.");
        }
    }

    [Fact]
    public void FallsBackWhenNoConfigurationIsAvailable()
    {
        var palette = ColourPalette.Parse("0 Nothing useful here");

        Assert.Same(ColourPalette.Fallback, palette);
        Assert.True(palette.TryGet(0, out _));
    }

    [Theory]
    [InlineData("#B40000", 0xB4, 0x00, 0x00, 255)]
    [InlineData("#80FF0000", 0xFF, 0x00, 0x00, 0x80)]
    [InlineData("B40000", 0xB4, 0x00, 0x00, 255)]
    public void ParsesHexColoursWithAndWithoutAlpha(string text, byte r, byte g, byte b, byte a)
    {
        Assert.True(Rgba.TryParseHex(text, out var colour));
        Assert.Equal(new Rgba(r, g, b, a), colour);
    }

    [Theory]
    [InlineData("#12345")]
    [InlineData("nothex")]
    [InlineData("")]
    public void RejectsMalformedHexColours(string text) =>
        Assert.False(Rgba.TryParseHex(text, out _));
}

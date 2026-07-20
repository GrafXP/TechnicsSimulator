using TechnicsSim.LDraw;
using TechnicsSim.LDraw.Expansion;
using TechnicsSim.LDraw.Parsing;
using TechnicsSim.LDraw.Resolution;
using TechnicsSim.LDraw.Sources;

namespace TechnicsSim.Tests;

public sealed class ResolutionTests
{
    /// <summary>A stand-in official library holding one part and one primitive.</summary>
    private static InMemoryFileSource StubLibrary() => new InMemoryFileSource("stub library")
        .Add("parts/3001.dat", """
            0 Brick  2 x  4
            0 !LDRAW_ORG Part UPDATE 2020-01
            """)
        .Add("parts/embedded.dat", """
            0 The official file that an MPD-local section must outrank
            0 !LDRAW_ORG Part UPDATE 2020-01
            """)
        .Add("p/stud.dat", """
            0 Stud
            0 !LDRAW_ORG Primitive
            """)
        .Add("parts/s/1234s01.dat", """
            0 ~Some Subpart
            0 !LDRAW_ORG Subpart
            """);

    private static LDrawResolver ResolverFor(string mpdText, out LDrawParseResult parsed)
    {
        parsed = LDrawParser.Parse(mpdText, "test.mpd");
        return new LDrawResolver(parsed.Documents, [StubLibrary()]);
    }

    [Fact]
    public void MpdLocalSectionsOutrankTheOfficialLibrary()
    {
        // This is the `8275 - LS70.dat` case: a .dat suffix says nothing about where a
        // reference resolves, and the MPD's own definition has to win.
        var resolver = ResolverFor("""
            0 FILE main.ldr
            0 !LDRAW_ORG Model
            1 16 0 0 0 1 0 0 0 1 0 0 0 1 embedded.dat

            0 FILE embedded.dat
            0 An MPD-local part
            0 !LDRAW_ORG Unofficial_Part
            """, out _);

        var resolved = resolver.Resolve("embedded.dat");

        Assert.True(resolved.IsResolved);
        Assert.Equal(ResolutionOrigin.MpdLocal, resolved.Origin);
        Assert.Equal("An MPD-local part", resolved.Document!.Description);
        Assert.True(resolved.Document.IsUnofficial);
    }

    [Theory]
    [InlineData("3001.dat", ResolutionOrigin.LibraryPart)]
    [InlineData("stud.dat", ResolutionOrigin.LibraryPrimitive)]
    [InlineData("s/1234s01.dat", ResolutionOrigin.LibrarySubpart)]
    [InlineData("s\\1234s01.dat", ResolutionOrigin.LibrarySubpart)]
    [InlineData("PARTS/3001.DAT", ResolutionOrigin.LibraryPart)]
    public void ResolvesAcrossLibraryRootsAndSlashDirections(string name, ResolutionOrigin expected)
    {
        var resolver = ResolverFor("0 FILE main.ldr", out _);
        var resolved = resolver.Resolve(name);

        Assert.True(resolved.IsResolved);
        Assert.Equal(expected, resolved.Origin);
    }

    [Fact]
    public void ReportsMissingReferencesRatherThanThrowing()
    {
        var resolver = ResolverFor("0 FILE main.ldr", out _);
        var resolved = resolver.Resolve("no-such-part.dat");

        Assert.False(resolved.IsResolved);
        Assert.Equal(ResolutionFailure.Missing, resolved.Failure);
    }

    [Fact]
    public void PreservesTheRequestedNameForDiagnostics()
    {
        var resolver = ResolverFor("0 FILE main.ldr", out _);

        Assert.Equal("PARTS/3001.DAT", resolver.Resolve("PARTS/3001.DAT").RequestedName);

        // The cached second lookup must still echo back what the caller actually wrote.
        Assert.Equal("parts/3001.dat", resolver.Resolve("parts/3001.dat").RequestedName);
    }

    [Fact]
    public void ClassifiesByHeaderNotByExtension()
    {
        var parsed = LDrawParser.Parse("""
            0 FILE looks-like-a-part.dat
            0 !LDRAW_ORG Model
            """, "test.mpd");

        var document = parsed.Root;
        Assert.Equal(LogicalKind.Model, LogicalClassifier.Classify(document, ResolutionOrigin.MpdLocal));

        // And the reverse: a .ldr-suffixed section declaring itself a part is a part.
        var asPart = LDrawParser.Parse("""
            0 FILE looks-like-a-model.ldr
            0 !LDRAW_ORG Unofficial_Part
            """, "test.mpd").Root;
        Assert.Equal(LogicalKind.Part, LogicalClassifier.Classify(asPart, ResolutionOrigin.MpdLocal));
    }

    [Fact]
    public void FallsBackToProviderOriginWhenNoHeaderIsPresent()
    {
        var document = LDrawParser.Parse("0 No header here", "mystery.dat").Root;

        Assert.Equal(LogicalKind.Part, LogicalClassifier.Classify(document, ResolutionOrigin.LibraryPart));
        Assert.Equal(LogicalKind.Primitive, LogicalClassifier.Classify(document, ResolutionOrigin.LibraryPrimitive));
        Assert.Equal(LogicalKind.Subpart, LogicalClassifier.Classify(document, ResolutionOrigin.LibrarySubpart));
        Assert.Equal(LogicalKind.Model, LogicalClassifier.Classify(document, ResolutionOrigin.MpdLocal));
    }
}

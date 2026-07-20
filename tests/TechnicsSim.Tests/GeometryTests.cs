using System.Numerics;
using TechnicsSim.LDraw.Colours;
using TechnicsSim.LDraw.Geometry;
using TechnicsSim.LDraw.Parsing;
using TechnicsSim.LDraw.Resolution;
using TechnicsSim.LDraw.Sources;

namespace TechnicsSim.Tests;

public sealed class GeometryTests
{
    /// <summary>A triangle wound counter-clockwise when viewed from +Y, so its normal is +Y.</summary>
    private const string UpwardTriangle = "3 16 0 0 0  0 0 10  10 0 0";

    private static PartMesh Build(string partText, params (string Name, string Text)[] library)
    {
        var source = new InMemoryFileSource("stub");
        foreach (var (name, text) in library)
        {
            source.Add(name, text);
        }

        var parsed = LDrawParser.Parse(partText, "test.dat");
        var resolver = new LDrawResolver([], [source]);
        return new PartMeshBuilder(resolver).Build(parsed.Root);
    }

    private static Vector3 NormalOf(PartMesh mesh, int group = 0) => mesh.Groups[group].Normals[0];

    [Fact]
    public void BuildsTrianglesWithFlatNormals()
    {
        var mesh = Build($"0 BFC CERTIFY CCW{Environment.NewLine}{UpwardTriangle}");

        Assert.Equal(1, mesh.TriangleCount);
        Assert.Equal(Vector3.UnitY, NormalOf(mesh));
    }

    [Fact]
    public void SplitsQuadsIntoTwoTriangles()
    {
        var mesh = Build("""
            0 BFC CERTIFY CCW
            4 16 0 0 0  0 0 10  10 0 10  10 0 0
            """);

        Assert.Equal(2, mesh.TriangleCount);
        Assert.Equal(6, mesh.Groups[0].Positions.Length);

        // Both halves face the same way; a diagonal chosen inconsistently would flip one.
        Assert.All(mesh.Groups[0].Normals, n => Assert.Equal(Vector3.UnitY, n));
    }

    [Fact]
    public void ReversesWindingForAClockwiseFile()
    {
        var ccw = Build($"0 BFC CERTIFY CCW{Environment.NewLine}{UpwardTriangle}");
        var cw = Build($"0 BFC CERTIFY CW{Environment.NewLine}{UpwardTriangle}");

        Assert.Equal(Vector3.UnitY, NormalOf(ccw));
        Assert.Equal(-Vector3.UnitY, NormalOf(cw));
    }

    [Fact]
    public void HonoursAWindingChangeMidFile()
    {
        var mesh = Build($"""
            0 BFC CERTIFY CCW
            {UpwardTriangle}
            0 BFC CW
            {UpwardTriangle}
            """);

        // Same source triangle, opposite facing, so the two land in one group with
        // opposing normals.
        Assert.Equal(2, mesh.TriangleCount);
        Assert.Equal(Vector3.UnitY, mesh.Groups[0].Normals[0]);
        Assert.Equal(-Vector3.UnitY, mesh.Groups[0].Normals[3]);
    }

    [Fact]
    public void AReflectingTransformFlipsChildFacing()
    {
        // The child is mirrored on Y, which reverses the facing of everything inside it.
        // The triangle lies in the y=0 plane, so mirroring leaves its vertices exactly where
        // they were: the reversal is carried entirely by the winding rule, which is precisely
        // the case a determinant check has to get right.
        var mesh = Build(
            "0 BFC CERTIFY CCW\n1 16 0 0 0  1 0 0  0 -1 0  0 0 1  child.dat",
            ("parts/child.dat", $"0 BFC CERTIFY CCW{Environment.NewLine}{UpwardTriangle}"));

        Assert.Equal(1, mesh.TriangleCount);
        Assert.Equal(-Vector3.UnitY, NormalOf(mesh));
    }

    [Fact]
    public void InvertNextFlipsOnlyTheFollowingReference()
    {
        var child = ("parts/child.dat", $"0 BFC CERTIFY CCW{Environment.NewLine}{UpwardTriangle}");

        var inverted = Build("""
            0 BFC CERTIFY CCW
            0 BFC INVERTNEXT
            1 16 0 0 0 1 0 0 0 1 0 0 0 1 child.dat
            1 16 0 0 0 1 0 0 0 1 0 0 0 1 child.dat
            """, child);

        Assert.Equal(2, inverted.TriangleCount);

        // First reference inverted, second back to normal.
        Assert.Equal(-Vector3.UnitY, inverted.Groups[0].Normals[0]);
        Assert.Equal(Vector3.UnitY, inverted.Groups[0].Normals[3]);
    }

    [Fact]
    public void InvertNextAndAReflectingTransformCancel()
    {
        // Two flips compose back to the original facing. Getting this wrong is invisible on
        // most parts and produces inside-out geometry on a handful.
        var mesh = Build(
            """
            0 BFC CERTIFY CCW
            0 BFC INVERTNEXT
            1 16 0 0 0  1 0 0  0 -1 0  0 0 1  child.dat
            """,
            ("parts/child.dat", $"0 BFC CERTIFY CCW{Environment.NewLine}{UpwardTriangle}"));

        Assert.Equal(Vector3.UnitY, NormalOf(mesh));
    }

    [Fact]
    public void RendersUncertifiedGeometryDoubleSided()
    {
        var uncertified = Build(UpwardTriangle);
        var certified = Build($"0 BFC CERTIFY CCW{Environment.NewLine}{UpwardTriangle}");

        Assert.True(uncertified.Groups[0].DoubleSided);
        Assert.False(certified.Groups[0].DoubleSided);
    }

    [Fact]
    public void NoClipDisablesCullingWithoutDecertifying()
    {
        var mesh = Build($"""
            0 BFC CERTIFY CCW
            0 BFC NOCLIP
            {UpwardTriangle}
            """);

        Assert.True(mesh.Groups[0].DoubleSided);
    }

    [Fact]
    public void KeepsColour16UnresolvedSoOneBufferServesEveryColour()
    {
        var mesh = Build($"0 BFC CERTIFY CCW{Environment.NewLine}{UpwardTriangle}");

        // The geometry cache must not bake a colour in; instance colour is applied later.
        Assert.Equal(ColourPalette.InheritedSurfaceCode, mesh.Groups[0].ColourCode);
    }

    [Fact]
    public void KeepsConcreteColoursSeparateFromInheritedOnes()
    {
        var mesh = Build($"""
            0 BFC CERTIFY CCW
            {UpwardTriangle}
            3 4 0 0 0  0 0 10  10 0 0
            """);

        Assert.Equal(2, mesh.Groups.Length);
        Assert.Contains(mesh.Groups, g => g.ColourCode == ColourPalette.InheritedSurfaceCode);
        Assert.Contains(mesh.Groups, g => g.ColourCode == 4);
    }

    [Fact]
    public void PropagatesAConcreteColourIntoInheritingChildren()
    {
        var mesh = Build(
            "0 BFC CERTIFY CCW\n1 4 0 0 0 1 0 0 0 1 0 0 0 1 child.dat",
            ("parts/child.dat", $"0 BFC CERTIFY CCW{Environment.NewLine}{UpwardTriangle}"));

        Assert.Equal(4, Assert.Single(mesh.Groups).ColourCode);
    }

    [Fact]
    public void AppliesNestedTransformsToGeometry()
    {
        var mesh = Build(
            "0 BFC CERTIFY CCW\n1 16 100 0 0 1 0 0 0 1 0 0 0 1 child.dat",
            ("parts/child.dat", "0 BFC CERTIFY CCW\n3 16 0 0 0  0 0 10  10 0 0"));

        Assert.Equal(new Vector3(100, 0, 0), mesh.Groups[0].Positions[0]);
        Assert.Equal(new Vector3(110, 0, 0), mesh.Groups[0].Positions[2]);
    }

    [Fact]
    public void ComputesBoundsOverAllGeometry()
    {
        var mesh = Build("0 BFC CERTIFY CCW\n3 16 -5 0 0  0 0 10  10 3 0");

        Assert.Equal(new Vector3(-5, 0, 0), mesh.Bounds.Min);
        Assert.Equal(new Vector3(10, 3, 10), mesh.Bounds.Max);
    }

    [Fact]
    public void CollectsEdgesSeparatelyAndOnlyWhenAsked()
    {
        const string text = "0 BFC CERTIFY CCW\n2 24 0 0 0 10 0 0";

        var parsed = LDrawParser.Parse(text, "test.dat");
        var resolver = new LDrawResolver([], [new InMemoryFileSource("stub")]);

        var withEdges = new PartMeshBuilder(resolver, new MeshBuildOptions(IncludeEdges: true)).Build(parsed.Root);
        var without = new PartMeshBuilder(resolver, new MeshBuildOptions(IncludeEdges: false)).Build(parsed.Root);

        Assert.Equal(1, withEdges.EdgeSegmentCount);
        Assert.Equal(ColourPalette.InheritedEdgeCode, withEdges.EdgeGroups[0].ColourCode);
        Assert.Equal(0, without.EdgeSegmentCount);
    }

    [Fact]
    public void DropsCameraDependentOptionalLines()
    {
        // Type-5 lines are parsed but deliberately not emitted until solid rendering is stable.
        var mesh = Build("0 BFC CERTIFY CCW\n5 24 0 0 0 1 0 0 0 1 0 0 0 1");

        Assert.True(mesh.IsEmpty);
    }

    [Fact]
    public void SurvivesADegenerateFaceWithoutProducingNaN()
    {
        var mesh = Build("0 BFC CERTIFY CCW\n3 16 0 0 0  0 0 0  0 0 0");

        var normal = NormalOf(mesh);
        Assert.False(float.IsNaN(normal.X) || float.IsNaN(normal.Y) || float.IsNaN(normal.Z));
        Assert.Equal(1f, normal.Length(), 5);
    }

    [Fact]
    public void StopsOnACyclicGeometryReference()
    {
        var mesh = Build(
            "0 BFC CERTIFY CCW\n1 16 0 0 0 1 0 0 0 1 0 0 0 1 a.dat",
            ("parts/a.dat", "0 BFC CERTIFY CCW\n1 16 0 0 0 1 0 0 0 1 0 0 0 1 b.dat"),
            ("parts/b.dat", $"0 BFC CERTIFY CCW{Environment.NewLine}{UpwardTriangle}"
                + $"{Environment.NewLine}1 16 0 0 0 1 0 0 0 1 0 0 0 1 a.dat"));

        // The cycle terminates and the geometry reached before it is still emitted.
        Assert.Equal(1, mesh.TriangleCount);
    }

    [Fact]
    public void ReportsUnresolvedGeometryReferencesRatherThanThrowing()
    {
        var mesh = Build("0 BFC CERTIFY CCW\n1 16 0 0 0 1 0 0 0 1 0 0 0 1 missing.dat");

        Assert.Equal("missing.dat", Assert.Single(mesh.UnresolvedReferences));
    }

    [Fact]
    public void RepeatedReferencesToOneChildEmitItEachTime()
    {
        // The cycle guard must block re-entry, not repetition; a submodel used twice draws twice.
        var mesh = Build(
            """
            0 BFC CERTIFY CCW
            1 16 0 0 0 1 0 0 0 1 0 0 0 1 child.dat
            1 16 50 0 0 1 0 0 0 1 0 0 0 1 child.dat
            """,
            ("parts/child.dat", $"0 BFC CERTIFY CCW{Environment.NewLine}{UpwardTriangle}"));

        Assert.Equal(2, mesh.TriangleCount);
    }
}

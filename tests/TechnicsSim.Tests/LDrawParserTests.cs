using System.Numerics;
using TechnicsSim.LDraw.Ast;
using TechnicsSim.LDraw.Parsing;

namespace TechnicsSim.Tests;

public sealed class LDrawParserTests
{
    [Fact]
    public void ParsesEachLineType()
    {
        var result = LDrawParser.Parse("""
            0 A description
            1 16 10 20 30 1 0 0 0 1 0 0 0 1 3001.dat
            2 24 0 0 0 1 1 1
            3 16 0 0 0 1 0 0 0 1 0
            4 16 0 0 0 1 0 0 1 1 0 0 1 0
            5 24 0 0 0 1 0 0 0 1 0 0 0 1
            """, "test.ldr");

        Assert.Empty(result.Issues);
        var commands = result.Root.Commands;

        Assert.IsType<MetaCommand>(commands[0]);
        Assert.IsType<SubfileReference>(commands[1]);
        Assert.IsType<EdgeLine>(commands[2]);
        Assert.IsType<Triangle>(commands[3]);
        Assert.IsType<Quad>(commands[4]);
        Assert.IsType<OptionalLine>(commands[5]);
    }

    [Fact]
    public void PreservesTargetNamesContainingSpaces()
    {
        var result = LDrawParser.Parse(
            "1 16 0 0 0 1 0 0 0 1 0 0 0 1 8275 - LS70.dat", "test.ldr");

        var reference = Assert.IsType<SubfileReference>(result.Root.Commands[0]);
        Assert.Equal("8275 - LS70.dat", reference.TargetName);
    }

    [Fact]
    public void RecordsIssuesInsteadOfDroppingBadLinesSilently()
    {
        var result = LDrawParser.Parse("""
            1 16 0 0 0 1 0 0 0 1 0 0 0 1
            3 16 0 0 0 not-a-number 0 0 0 1 0
            9 something
            """, "test.ldr");

        Assert.Equal(3, result.Issues.Length);
        Assert.All(result.Issues, issue => Assert.Equal("test.ldr", issue.DocumentName));
        Assert.Contains(result.Issues, issue => issue.Reason.Contains("no target name"));
        Assert.Contains(result.Issues, issue => issue.Reason.Contains("Unparsable number"));
        Assert.Contains(result.Issues, issue => issue.Reason.Contains("Unknown line type"));
    }

    [Theory]
    [InlineData("0x2FF00FF", 0x2FF00FF)]
    [InlineData("#2FF00FF", 0x2FF00FF)]
    [InlineData("16", 16)]
    public void ParsesDirectAndIndexedColours(string token, int expected)
    {
        var result = LDrawParser.Parse(
            $"1 {token} 0 0 0 1 0 0 0 1 0 0 0 1 3001.dat", "test.ldr");

        var reference = Assert.IsType<SubfileReference>(result.Root.Commands[0]);
        Assert.Equal(expected, reference.Colour);
    }

    [Fact]
    public void SplitsMpdSectionsAndKeepsTheFirstAsRoot()
    {
        var result = LDrawParser.Parse("""
            0 FILE main.ldr
            0 Main model
            0 !LDRAW_ORG Model
            1 16 0 0 0 1 0 0 0 1 0 0 0 1 sub.ldr

            0 FILE sub.ldr
            0 A submodel
            0 !LDRAW_ORG Model
            1 16 0 0 0 1 0 0 0 1 0 0 0 1 3001.dat
            """, "ignored.mpd");

        Assert.Equal(2, result.Documents.Length);
        Assert.Equal("main.ldr", result.Root.Name);
        Assert.Equal("Main model", result.Root.Description);
        Assert.Equal("sub.ldr", result.Documents[1].Name);
        Assert.All(result.Documents, d => Assert.Equal(LDrawOrgKind.Model, d.OrgKind));
    }

    [Fact]
    public void EndsASectionAtNoFile()
    {
        var result = LDrawParser.Parse("""
            0 FILE main.ldr
            1 16 0 0 0 1 0 0 0 1 0 0 0 1 3001.dat
            0 NOFILE
            0 This trailing comment belongs to no section.
            """, "ignored.mpd");

        Assert.Equal(2, result.Documents.Length);
        Assert.Equal("main.ldr", result.Documents[0].Name);
        Assert.Single(result.Documents[0].Commands);

        // Content after NOFILE falls back to the file's default name.
        Assert.Equal("ignored.mpd", result.Documents[1].Name);
    }

    [Fact]
    public void TreatsAFileWithoutFileMetasAsASingleDocument()
    {
        var result = LDrawParser.Parse("""
            0 Technic Axle  4
            0 !LDRAW_ORG Part UPDATE 2020-01
            1 16 0 0 0 1 0 0 0 1 0 0 0 1 axle.dat
            """, "3705.dat");

        Assert.Single(result.Documents);
        Assert.Equal("3705.dat", result.Root.Name);
        Assert.Equal(LDrawOrgKind.Part, result.Root.OrgKind);
        Assert.False(result.Root.IsUnofficial);
        Assert.Equal("Technic Axle  4", result.Root.Description);
    }

    [Theory]
    [InlineData("Model", LDrawOrgKind.Model, false)]
    [InlineData("MODEL", LDrawOrgKind.Model, false)]
    [InlineData("Unofficial_Model", LDrawOrgKind.Model, true)]
    [InlineData("Unofficial_Part", LDrawOrgKind.Part, true)]
    [InlineData("Part UPDATE 2020-01", LDrawOrgKind.Part, false)]
    [InlineData("Subpart UPDATE 2010-03", LDrawOrgKind.Subpart, false)]
    [InlineData("48_Primitive", LDrawOrgKind.HiResPrimitive, false)]
    [InlineData("8_Primitive", LDrawOrgKind.LowResPrimitive, false)]
    [InlineData("Shortcut", LDrawOrgKind.Shortcut, false)]
    public void ParsesLDrawOrgVariants(string value, LDrawOrgKind expected, bool unofficial)
    {
        var result = LDrawParser.Parse($"0 Description{Environment.NewLine}0 !LDRAW_ORG {value}", "x.dat");

        Assert.Equal(expected, result.Root.OrgKind);
        Assert.Equal(unofficial, result.Root.IsUnofficial);
    }

    /// <summary>
    /// The type-1 matrix mapping documented in PLAN.md. Asserted numerically because visual
    /// inspection of a transposed matrix is exactly the mistake this test exists to catch.
    /// </summary>
    [Fact]
    public void MapsTypeOneFieldsOntoTheDocumentedMatrix()
    {
        // x y z = 10 20 30, then a..i as a distinctive non-symmetric matrix.
        var result = LDrawParser.Parse(
            "1 16 10 20 30  1 2 3  4 5 6  7 8 9  3001.dat", "test.ldr");

        var m = Assert.IsType<SubfileReference>(result.Root.Commands[0]).Transform;

        // LDraw: x' = a*px + b*py + c*pz + x, and equivalently row-vector p * M.
        Assert.Equal(new Vector3(10, 20, 30), Vector3.Transform(Vector3.Zero, m));
        Assert.Equal(new Vector3(1 + 10, 4 + 20, 7 + 30), Vector3.Transform(Vector3.UnitX, m));
        Assert.Equal(new Vector3(2 + 10, 5 + 20, 8 + 30), Vector3.Transform(Vector3.UnitY, m));
        Assert.Equal(new Vector3(3 + 10, 6 + 20, 9 + 30), Vector3.Transform(Vector3.UnitZ, m));
    }

    /// <summary>
    /// Locks the composition order for nested references. A child transform applies before its
    /// parent's, which for row vectors is <c>child * parent</c>. Reversing this is a silent
    /// error that only shows up as subtly misplaced geometry deep in a model.
    /// </summary>
    [Fact]
    public void ComposesNestedTransformsChildBeforeParent()
    {
        // Parent: translate +100 on X. Child: rotate 90 degrees about Z, then translate +10 X.
        var parent = ParseTransform("1 16 100 0 0  1 0 0  0 1 0  0 0 1  child.ldr");
        var child = ParseTransform("1 16 10 0 0  0 -1 0  1 0 0  0 0 1  3001.dat");

        var composed = Matrix4x4.Multiply(child, parent);

        // A point at the child's local origin lands at parent(child(0)) = (110, 0, 0).
        Assert.Equal(new Vector3(110, 0, 0), Vector3.Transform(Vector3.Zero, composed));

        // The child's local +X axis is rotated onto world +Y before the parent translates.
        Assert.Equal(new Vector3(110, 1, 0), Vector3.Transform(Vector3.UnitX, composed));

        // The reversed order would put the point somewhere else entirely.
        Assert.NotEqual(
            Vector3.Transform(Vector3.Zero, composed),
            Vector3.Transform(Vector3.Zero, Matrix4x4.Multiply(parent, child)));
    }

    private static Matrix4x4 ParseTransform(string line) =>
        Assert.IsType<SubfileReference>(LDrawParser.Parse(line, "t.ldr").Root.Commands[0]).Transform;
}

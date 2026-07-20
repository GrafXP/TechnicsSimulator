using System.Numerics;
using TechnicsSim.Wpf.Rendering;

namespace TechnicsSim.Wpf.Tests;

/// <summary>
/// Pins the one place LDraw coordinates become renderer coordinates. Getting this wrong shows
/// up as an upside-down model, or worse, as inverted geometry that looks almost right.
/// </summary>
public sealed class LDrawAxesTests
{
    [Fact]
    public void MapsLDrawDownToRendererUp()
    {
        // LDraw's +Y points down, so a point below the origin in LDraw is above it on screen.
        var below = new Vector3(0, 100, 0);

        Assert.Equal(new Vector3(0, -100, 0), LDrawAxes.PointToRenderer(below));
    }

    [Fact]
    public void LeavesTheXAxisAlone()
    {
        Assert.Equal(new Vector3(50, 0, 0), LDrawAxes.PointToRenderer(new Vector3(50, 0, 0)));
    }

    /// <summary>
    /// The conversion must be a rotation, not a reflection. A reflection would flip the facing
    /// of every triangle and quietly undo the BFC work done while building meshes.
    /// </summary>
    [Fact]
    public void PreservesHandedness()
    {
        var determinant = LDrawAxes.ToRenderer.GetDeterminant();

        Assert.Equal(1f, determinant, 5);
        Assert.True(determinant > 0, "A negative determinant would invert every face.");
    }

    [Fact]
    public void IsItsOwnInverse()
    {
        var original = new Vector3(12, -34, 56);

        var roundTripped = LDrawAxes.PointToLDraw(LDrawAxes.PointToRenderer(original));

        Assert.Equal(original, roundTripped);
    }

    [Fact]
    public void PreservesDistancesAndAngles()
    {
        var a = new Vector3(10, 20, 30);
        var b = new Vector3(-5, 7, 2);

        var ra = LDrawAxes.PointToRenderer(a);
        var rb = LDrawAxes.PointToRenderer(b);

        Assert.Equal((a - b).Length(), (ra - rb).Length(), 4);
        Assert.Equal(Vector3.Dot(a, b), Vector3.Dot(ra, rb), 3);
    }

    [Fact]
    public void ConvertsAnInstanceTransformByComposingAfterIt()
    {
        // An instance placed 100 LDU "down" (+Y) must render 100 units up.
        var placement = Matrix4x4.CreateTranslation(0, 100, 0);

        var rendered = LDrawAxes.TransformToRenderer(placement);

        Assert.Equal(new Vector3(0, -100, 0), Vector3.Transform(Vector3.Zero, rendered));
    }

    [Fact]
    public void KeepsARotatedInstanceRigid()
    {
        var placement = Matrix4x4.Multiply(
            Matrix4x4.CreateRotationZ(0.7f), Matrix4x4.CreateTranslation(10, 20, 30));

        var rendered = LDrawAxes.TransformToRenderer(placement);

        Assert.Equal(1f, rendered.GetDeterminant(), 4);
    }
}

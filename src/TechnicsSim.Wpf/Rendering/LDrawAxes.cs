using System.Numerics;

namespace TechnicsSim.Wpf.Rendering;

/// <summary>
/// The single boundary where LDraw coordinates become renderer coordinates.
///
/// LDraw is right-handed with <b>+Y pointing down</b>. The viewer wants +Y up. The conversion
/// is a 180-degree rotation about X, mapping <c>(x, y, z)</c> to <c>(x, -y, -z)</c>.
///
/// A rotation is used rather than the more obvious <c>scale(1, -1, 1)</c> because a scale
/// reflects: its determinant is negative, so it would silently invert the facing of every
/// triangle in the model and undo the BFC work done during mesh building. This transform has
/// determinant +1 and leaves winding alone.
///
/// Everything on the core side of this boundary stays in LDraw coordinates and LDU.
/// </summary>
public static class LDrawAxes
{
    /// <summary>LDraw model space to renderer world space.</summary>
    public static readonly Matrix4x4 ToRenderer = new(
        1, 0, 0, 0,
        0, -1, 0, 0,
        0, 0, -1, 0,
        0, 0, 0, 1);

    /// <summary>Renderer world space back to LDraw model space. The rotation is its own inverse.</summary>
    public static readonly Matrix4x4 ToLDraw = ToRenderer;

    public static Vector3 PointToRenderer(Vector3 ldraw) => Vector3.Transform(ldraw, ToRenderer);

    public static Vector3 PointToLDraw(Vector3 renderer) => Vector3.Transform(renderer, ToLDraw);

    /// <summary>Converts an instance transform expressed in LDraw space.</summary>
    public static Matrix4x4 TransformToRenderer(Matrix4x4 ldraw) => Matrix4x4.Multiply(ldraw, ToRenderer);
}

using System.Collections.Immutable;
using System.Numerics;

namespace TechnicsSim.LDraw.Geometry;

/// <summary>An axis-aligned bounding box in LDU.</summary>
public readonly record struct Bounds(Vector3 Min, Vector3 Max)
{
    public static Bounds Empty => new(
        new Vector3(float.PositiveInfinity), new Vector3(float.NegativeInfinity));

    public bool IsEmpty => Min.X > Max.X;

    public Vector3 Centre => IsEmpty ? Vector3.Zero : (Min + Max) * 0.5f;

    public Vector3 Size => IsEmpty ? Vector3.Zero : Max - Min;

    public Bounds Include(Vector3 point) =>
        new(Vector3.Min(Min, point), Vector3.Max(Max, point));

    public Bounds Union(Bounds other) =>
        other.IsEmpty ? this : IsEmpty ? other : new(Vector3.Min(Min, other.Min), Vector3.Max(Max, other.Max));

    /// <summary>The eight corners, used to bound a transformed child correctly.</summary>
    public IEnumerable<Vector3> Corners()
    {
        if (IsEmpty)
        {
            yield break;
        }

        for (var i = 0; i < 8; i++)
        {
            yield return new Vector3(
                (i & 1) == 0 ? Min.X : Max.X,
                (i & 2) == 0 ? Min.Y : Max.Y,
                (i & 4) == 0 ? Min.Z : Max.Z);
        }
    }
}

/// <summary>
/// Triangles from one part that share a colour code and a culling mode.
///
/// The colour code is kept <em>unresolved</em>. A group carrying
/// <see cref="Colours.ColourPalette.InheritedSurfaceCode"/> takes the colour of whichever
/// instance draws it, which is what lets one cached vertex buffer serve every colour a part
/// appears in rather than baking colour into geometry.
/// </summary>
public sealed record MeshGroup(
    int ColourCode,
    bool DoubleSided,
    ImmutableArray<Vector3> Positions,
    ImmutableArray<Vector3> Normals,
    ImmutableArray<int> Indices)
{
    public int TriangleCount => Indices.Length / 3;
}

/// <summary>Edge segments from one part, kept separate as an optional render pass.</summary>
public sealed record EdgeGroup(
    int ColourCode,
    ImmutableArray<Vector3> Positions,
    ImmutableArray<int> Indices)
{
    public int SegmentCount => Indices.Length / 2;
}

/// <summary>
/// The cached geometry of one logical part, in the part's own coordinate system and LDU.
///
/// Built once per canonical part name and shared by every instance, which is what keeps 1,630
/// track links to a single vertex buffer.
/// </summary>
public sealed record PartMesh(
    string CanonicalName,
    ImmutableArray<MeshGroup> Groups,
    ImmutableArray<EdgeGroup> EdgeGroups,
    Bounds Bounds,
    int SourceTriangleCount,
    int ReferenceNodeCount,
    ImmutableArray<string> UnresolvedReferences)
{
    public int TriangleCount => Groups.Sum(g => g.TriangleCount);

    public int VertexCount => Groups.Sum(g => g.Positions.Length);

    public int EdgeSegmentCount => EdgeGroups.Sum(g => g.SegmentCount);

    /// <summary>True when the part contributed no drawable geometry at all.</summary>
    public bool IsEmpty => Groups.Length == 0 && EdgeGroups.Length == 0;
}

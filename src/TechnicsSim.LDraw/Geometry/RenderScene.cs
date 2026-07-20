using System.Collections.Immutable;
using System.Numerics;
using TechnicsSim.LDraw.Colours;
using TechnicsSim.LDraw.Expansion;

namespace TechnicsSim.LDraw.Geometry;

/// <summary>One drawable placement of a logical part, in model space and LDU.</summary>
public sealed record SceneInstance(
    string InstanceId,
    string CanonicalPartName,
    string PartName,
    Matrix4x4 Transform,
    ResolvedColour Colour,
    Bounds WorldBounds);

/// <summary>
/// A set of instances that share one vertex buffer and one material, so the renderer can draw
/// them in a single instanced call.
///
/// A batch is keyed by part, mesh group, and resolved colour. That grouping is what keeps the
/// 1,630 8275 track links to one vertex buffer and one draw call rather than 1,630 of each.
/// </summary>
/// <param name="InstanceIndices">Indices into <see cref="RenderScene.Instances"/>, which is
/// what maps a rendered instance back to its hierarchical logical instance ID.</param>
public sealed record InstanceBatch(
    string CanonicalPartName,
    int MeshGroupIndex,
    ResolvedColour Colour,
    bool DoubleSided,
    ImmutableArray<int> InstanceIndices)
{
    public int InstanceCount => InstanceIndices.Length;
}

/// <summary>A renderer-independent description of everything to draw.</summary>
/// <param name="StaticGeometry">
/// Geometry written directly into the model's own sections, drawn once at the model origin.
/// This is the generated LDCad hose and spring fallback mesh. It is decorative, is not a
/// logical part, and is not selectable or animated.
/// </param>
public sealed record RenderScene(
    ImmutableArray<SceneInstance> Instances,
    ImmutableArray<InstanceBatch> Batches,
    ImmutableDictionary<string, PartMesh> Meshes,
    PartMesh StaticGeometry,
    Bounds Bounds,
    ImmutableArray<string> PartsWithoutGeometry)
{
    /// <summary>Distinct vertex buffers the renderer must upload.</summary>
    public int DistinctMeshGroups => Batches
        .Select(b => (b.CanonicalPartName, b.MeshGroupIndex))
        .Distinct()
        .Count();

    public int TriangleCount => Batches.Sum(
        b => Meshes[b.CanonicalPartName].Groups[b.MeshGroupIndex].TriangleCount * b.InstanceCount)
        + StaticGeometry.TriangleCount;

    /// <summary>Triangles actually uploaded, before instancing multiplies them on the GPU.</summary>
    public int UploadedTriangleCount => Batches
        .Select(b => (b.CanonicalPartName, b.MeshGroupIndex))
        .Distinct()
        .Sum(k => Meshes[k.CanonicalPartName].Groups[k.MeshGroupIndex].TriangleCount)
        + StaticGeometry.TriangleCount;

    public SceneInstance? FindInstance(string instanceId) =>
        Instances.FirstOrDefault(i => i.InstanceId == instanceId);
}

/// <summary>
/// Turns an expanded model into batched, instanced draw data.
///
/// Colour is resolved here rather than in the mesh builder. Geometry is cached per part with
/// unresolved colour slots, and a part appearing in three colours becomes three batches over
/// the same vertex buffer instead of three copies of the geometry.
/// </summary>
public sealed class SceneBuilder
{
    private readonly PartMeshCache _meshes;
    private readonly ColourPalette _palette;

    public SceneBuilder(PartMeshCache meshes, ColourPalette palette)
    {
        _meshes = meshes;
        _palette = palette;
    }

    public RenderScene Build(ModelExpansion expansion)
    {
        var instances = ImmutableArray.CreateBuilder<SceneInstance>(expansion.Instances.Length);
        var meshes = ImmutableDictionary.CreateBuilder<string, PartMesh>(StringComparer.Ordinal);
        var withoutGeometry = new SortedSet<string>(StringComparer.Ordinal);
        var sceneBounds = Bounds.Empty;

        // Buckets keyed by part and the colour the instance was drawn with. Colour 16 has
        // already been resolved into a concrete code by logical expansion.
        var buckets = new Dictionary<(string Part, int Colour), List<int>>();

        for (var i = 0; i < expansion.Instances.Length; i++)
        {
            var logical = expansion.Instances[i];
            var mesh = _meshes.Get(logical.CanonicalPartName);
            meshes[logical.CanonicalPartName] = mesh;

            if (mesh.IsEmpty)
            {
                withoutGeometry.Add(logical.CanonicalPartName);
            }

            var colour = _palette.Resolve(logical.Colour, _palette.DefaultContext);
            var worldBounds = TransformBounds(mesh.Bounds, logical.Transform);
            sceneBounds = sceneBounds.Union(worldBounds);

            instances.Add(new SceneInstance(
                logical.InstanceId,
                logical.CanonicalPartName,
                logical.PartName,
                logical.Transform,
                colour,
                worldBounds));

            var key = (logical.CanonicalPartName, logical.Colour);
            if (!buckets.TryGetValue(key, out var list))
            {
                buckets[key] = list = [];
            }

            list.Add(i);
        }

        // Generated hose and spring fallback meshes live in the model's sections, not in any
        // part, so they are built once and drawn statically.
        var staticGeometry = _meshes.BuildInlineGeometry(expansion.Root);
        sceneBounds = sceneBounds.Union(staticGeometry.Bounds);

        var batches = ImmutableArray.CreateBuilder<InstanceBatch>();

        // Ordinal ordering keeps batch order deterministic so mesh statistics are comparable
        // between runs and machines.
        foreach (var ((part, instanceColour), indices) in buckets
            .OrderBy(b => b.Key.Part, StringComparer.Ordinal)
            .ThenBy(b => b.Key.Colour)
            .Select(b => (b.Key, b.Value)))
        {
            var mesh = meshes[part];
            var context = _palette.Resolve(instanceColour, _palette.DefaultContext);

            for (var g = 0; g < mesh.Groups.Length; g++)
            {
                var group = mesh.Groups[g];
                batches.Add(new InstanceBatch(
                    part,
                    g,
                    _palette.Resolve(group.ColourCode, context),
                    group.DoubleSided,
                    indices.ToImmutableArray()));
            }
        }

        return new RenderScene(
            instances.ToImmutable(),
            batches.ToImmutable(),
            meshes.ToImmutable(),
            staticGeometry,
            sceneBounds,
            withoutGeometry.ToImmutableArray());
    }

    /// <summary>
    /// Bounds a transformed box by its eight transformed corners. Transforming only min and max
    /// is wrong under rotation, which is most LDraw references.
    /// </summary>
    private static Bounds TransformBounds(Bounds local, Matrix4x4 transform)
    {
        if (local.IsEmpty)
        {
            return Bounds.Empty;
        }

        var result = Bounds.Empty;
        foreach (var corner in local.Corners())
        {
            result = result.Include(Vector3.Transform(corner, transform));
        }

        return result;
    }
}

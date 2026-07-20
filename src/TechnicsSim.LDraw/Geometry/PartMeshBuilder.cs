using System.Collections.Immutable;
using System.Numerics;
using TechnicsSim.LDraw.Ast;
using TechnicsSim.LDraw.Colours;
using TechnicsSim.LDraw.Resolution;

namespace TechnicsSim.LDraw.Geometry;

/// <summary>Options controlling how much geometry is produced.</summary>
/// <param name="IncludeEdges">Collect type-2 edge lines as an optional render pass.</param>
/// <param name="MaxDepth">
/// A guard against pathological nesting. LDraw part trees are shallow; anything deeper than
/// this is a malformed file rather than legitimate geometry.
/// </param>
public sealed record MeshBuildOptions(bool IncludeEdges = true, int MaxDepth = 64)
{
    public static MeshBuildOptions Default { get; } = new();
}

/// <summary>
/// Flattens one part's geometry tree into cached, renderer-independent triangles.
///
/// This is the geometry traversal, distinct from logical part expansion: it descends all the
/// way through subparts and primitives, because those are the part's own geometry rather than
/// independent LEGO pieces.
///
/// Type-5 optional lines are parsed but not emitted. They are camera-dependent and PLAN.md
/// defers them until solid rendering and mechanics are stable.
/// </summary>
public sealed class PartMeshBuilder
{
    private readonly LDrawResolver _resolver;
    private readonly MeshBuildOptions _options;

    public PartMeshBuilder(LDrawResolver resolver, MeshBuildOptions? options = null)
    {
        _resolver = resolver;
        _options = options ?? MeshBuildOptions.Default;
    }

    /// <summary>Accumulates triangles into buckets keyed by colour code and culling mode.</summary>
    private sealed class Accumulator
    {
        public readonly Dictionary<(int Colour, bool DoubleSided), List<Vector3>> Positions = [];
        public readonly Dictionary<(int Colour, bool DoubleSided), List<Vector3>> Normals = [];
        public readonly Dictionary<int, List<Vector3>> EdgePositions = [];
        public readonly List<string> Unresolved = [];
        public Bounds Bounds = Bounds.Empty;
        public int SourceTriangles;
        public int ReferenceNodes;
    }

    public PartMesh Build(LDrawDocument document)
    {
        var accumulator = new Accumulator();
        var visiting = new HashSet<string>(StringComparer.Ordinal);

        Descend(document, Matrix4x4.Identity, BfcState.Initial, ColourPalette.InheritedSurfaceCode,
            accumulator, visiting, 0, stopAtLogicalParts: false);

        return Finish(document.CanonicalName, accumulator);
    }

    /// <summary>
    /// Builds the geometry written directly into a model's own sections, stopping wherever a
    /// logical part begins.
    ///
    /// This is the generated fallback mesh for LDCad hoses and springs: the 8458 models carry
    /// tens of thousands of such lines. The <c>PATH_*</c> and <c>SPRING_*</c> generator metas
    /// are deliberately ignored, so this geometry is drawn exactly once and is not
    /// double-instantiated alongside a generated equivalent.
    ///
    /// The part boundary rule is the same one logical expansion uses, so every triangle is
    /// either here or inside an instanced part, never both and never neither.
    /// </summary>
    public PartMesh BuildModelInlineGeometry(LDrawDocument root)
    {
        var accumulator = new Accumulator();
        var visiting = new HashSet<string>(StringComparer.Ordinal);

        Descend(root, Matrix4x4.Identity, BfcState.Initial, ColourPalette.InheritedSurfaceCode,
            accumulator, visiting, 0, stopAtLogicalParts: true);

        return Finish($"{root.CanonicalName}#inline", accumulator);
    }

    private static PartMesh Finish(string name, Accumulator accumulator) => new(
        name,
        BuildGroups(accumulator),
        BuildEdgeGroups(accumulator),
        accumulator.Bounds,
        accumulator.SourceTriangles,
        accumulator.ReferenceNodes,
        accumulator.Unresolved.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray());

    private void Descend(
        LDrawDocument document,
        Matrix4x4 transform,
        BfcState state,
        int contextColour,
        Accumulator accumulator,
        HashSet<string> visiting,
        int depth,
        bool stopAtLogicalParts)
    {
        if (depth > _options.MaxDepth || !visiting.Add(document.CanonicalName))
        {
            return;
        }

        try
        {
            var invertNext = false;

            foreach (var command in document.Commands)
            {
                switch (command)
                {
                    case MetaCommand { Keyword: "BFC" } meta:
                        state = state.ApplyMeta(meta.Arguments, out var flagged);

                        // INVERTNEXT applies to the next type-1 line only, so it is not
                        // cleared here; it is cleared when that reference consumes it.
                        invertNext |= flagged;
                        continue;

                    case SubfileReference reference:
                        DescendInto(reference, transform, state, contextColour, accumulator,
                            visiting, depth, invertNext, stopAtLogicalParts);
                        invertNext = false;
                        continue;

                    case Triangle triangle:
                        accumulator.SourceTriangles++;
                        EmitTriangle(
                            Transform(triangle.A, transform),
                            Transform(triangle.B, transform),
                            Transform(triangle.C, transform),
                            ResolveCode(triangle.Colour, contextColour),
                            state,
                            accumulator);
                        continue;

                    case Quad quad:
                        accumulator.SourceTriangles++;
                        EmitQuad(
                            Transform(quad.A, transform),
                            Transform(quad.B, transform),
                            Transform(quad.C, transform),
                            Transform(quad.D, transform),
                            ResolveCode(quad.Colour, contextColour),
                            state,
                            accumulator);
                        continue;

                    case EdgeLine edge when _options.IncludeEdges:
                        EmitEdge(
                            Transform(edge.A, transform),
                            Transform(edge.B, transform),
                            ResolveCode(edge.Colour, contextColour),
                            accumulator);
                        continue;

                    // Type-5 optional lines are deliberately dropped for now.
                    default:
                        continue;
                }
            }
        }
        finally
        {
            visiting.Remove(document.CanonicalName);
        }
    }

    private void DescendInto(
        SubfileReference reference,
        Matrix4x4 transform,
        BfcState state,
        int contextColour,
        Accumulator accumulator,
        HashSet<string> visiting,
        int depth,
        bool invertNext,
        bool stopAtLogicalParts)
    {
        var resolved = _resolver.Resolve(reference.TargetName);
        if (!resolved.IsResolved)
        {
            accumulator.Unresolved.Add(reference.TargetName);
            return;
        }

        if (stopAtLogicalParts
            && Expansion.LogicalClassifier.Classify(resolved.Document!, resolved.Origin)
               != Expansion.LogicalKind.Model)
        {
            // A logical part begins here. It is drawn as an instance, so descending would
            // duplicate its geometry into the static mesh as well.
            return;
        }

        accumulator.ReferenceNodes++;

        var childTransform = Matrix4x4.Multiply(reference.Transform, transform);
        var childColour = ResolveCode(reference.Colour, contextColour);
        var childState = state.ForChild(Reflects(reference.Transform), invertNext);

        Descend(resolved.Document!, childTransform, childState, childColour, accumulator, visiting,
            depth + 1, stopAtLogicalParts);
    }

    /// <summary>
    /// Colour 16 keeps deferring to the caller, so an unresolved 16 stays 16 and is finally
    /// resolved at instance time. Any concrete code, including 24 and direct colours, wins.
    /// </summary>
    private static int ResolveCode(int code, int contextColour) =>
        code == ColourPalette.InheritedSurfaceCode ? contextColour : code;

    /// <summary>True when a transform reflects, which reverses polygon facing.</summary>
    private static bool Reflects(Matrix4x4 m)
    {
        var x = new Vector3(m.M11, m.M12, m.M13);
        var y = new Vector3(m.M21, m.M22, m.M23);
        var z = new Vector3(m.M31, m.M32, m.M33);
        return Vector3.Dot(Vector3.Cross(x, y), z) < 0f;
    }

    private static Vector3 Transform(Vector3 point, Matrix4x4 m) => Vector3.Transform(point, m);

    private void EmitTriangle(
        Vector3 a, Vector3 b, Vector3 c, int colour, BfcState state, Accumulator accumulator)
    {
        if (state.ReversesWinding)
        {
            (b, c) = (c, b);
        }

        var normal = FaceNormal(a, b, c);
        var key = (colour, !state.ShouldCull);

        var positions = accumulator.Positions.TryGetValue(key, out var p) ? p : accumulator.Positions[key] = [];
        var normals = accumulator.Normals.TryGetValue(key, out var n) ? n : accumulator.Normals[key] = [];

        positions.Add(a);
        positions.Add(b);
        positions.Add(c);
        normals.Add(normal);
        normals.Add(normal);
        normals.Add(normal);

        accumulator.Bounds = accumulator.Bounds.Include(a).Include(b).Include(c);
    }

    /// <summary>
    /// Splits a quad on the a-c diagonal, always in the same order, so a rebuild produces
    /// byte-identical buffers and golden mesh statistics stay comparable.
    /// </summary>
    private void EmitQuad(
        Vector3 a, Vector3 b, Vector3 c, Vector3 d, int colour, BfcState state, Accumulator accumulator)
    {
        EmitTriangle(a, b, c, colour, state, accumulator);
        EmitTriangle(a, c, d, colour, state, accumulator);
    }

    private static void EmitEdge(Vector3 a, Vector3 b, int colour, Accumulator accumulator)
    {
        var list = accumulator.EdgePositions.TryGetValue(colour, out var e)
            ? e
            : accumulator.EdgePositions[colour] = [];

        list.Add(a);
        list.Add(b);
    }

    /// <summary>
    /// A flat per-face normal. Degenerate faces, which LDraw parts do contain, fall back to a
    /// unit vector rather than producing NaN and poisoning the vertex buffer.
    /// </summary>
    private static Vector3 FaceNormal(Vector3 a, Vector3 b, Vector3 c)
    {
        var normal = Vector3.Cross(b - a, c - a);
        var length = normal.Length();
        return length > 1e-9f ? normal / length : Vector3.UnitY;
    }

    private static ImmutableArray<MeshGroup> BuildGroups(Accumulator accumulator)
    {
        var groups = ImmutableArray.CreateBuilder<MeshGroup>();

        foreach (var key in accumulator.Positions.Keys
            .OrderBy(k => k.Colour)
            .ThenBy(k => k.DoubleSided))
        {
            var positions = accumulator.Positions[key];
            var normals = accumulator.Normals[key];

            // Flat shading duplicates every vertex, so indices are sequential. Smoothing is a
            // later refinement; faceted cylinders are acceptable for the first visual slice.
            var indices = ImmutableArray.CreateBuilder<int>(positions.Count);
            for (var i = 0; i < positions.Count; i++)
            {
                indices.Add(i);
            }

            groups.Add(new MeshGroup(
                key.Colour,
                key.DoubleSided,
                positions.ToImmutableArray(),
                normals.ToImmutableArray(),
                indices.MoveToImmutable()));
        }

        return groups.ToImmutable();
    }

    private static ImmutableArray<EdgeGroup> BuildEdgeGroups(Accumulator accumulator)
    {
        var groups = ImmutableArray.CreateBuilder<EdgeGroup>();

        foreach (var colour in accumulator.EdgePositions.Keys.Order())
        {
            var positions = accumulator.EdgePositions[colour];
            var indices = ImmutableArray.CreateBuilder<int>(positions.Count);
            for (var i = 0; i < positions.Count; i++)
            {
                indices.Add(i);
            }

            groups.Add(new EdgeGroup(colour, positions.ToImmutableArray(), indices.MoveToImmutable()));
        }

        return groups.ToImmutable();
    }
}

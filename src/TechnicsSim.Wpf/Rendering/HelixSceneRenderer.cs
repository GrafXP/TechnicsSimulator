using System.Collections.Immutable;
using System.Diagnostics;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using HelixToolkit;
using HelixToolkit.Maths;
using HelixToolkit.SharpDX;
using HelixToolkit.Wpf.SharpDX;
using TechnicsSim.LDraw.Colours;
using TechnicsSim.LDraw.Geometry;
using TechnicsSim.Mechanics.Features;
using TechnicsSim.Mechanics.Mating;
using Colour = TechnicsSim.LDraw.Colours.Rgba;
using HelixMesh = HelixToolkit.SharpDX.MeshGeometry3D;
using MediaColor = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace TechnicsSim.Wpf.Rendering;

/// <summary>
/// Draws a <see cref="RenderScene"/> with HelixToolkit.Wpf.SharpDX.
///
/// One instanced model is created per batch, so a part used 1,630 times uploads its geometry
/// once and is drawn with 1,630 instance transforms. Geometry buffers are additionally shared
/// between batches of the same part in different colours, because colour lives in the material
/// rather than in the vertex data.
/// </summary>
public sealed class HelixSceneRenderer : ISceneRenderer
{
    private readonly Viewport3DX _viewport;
    private readonly GroupModel3D _sceneRoot = new();
    private readonly GroupModel3D _edgeRoot = new();
    private readonly GroupModel3D _diagnosticsRoot = new();
    private readonly GroupModel3D _highlightRoot = new();
    private readonly InstanceIdentityMap _identities = new();

    private RenderScene? _scene;
    private ConnectionAnalysis? _mechanics;
    private bool _showEdges;
    private bool _showDiagnostics;

    public HelixSceneRenderer(Viewport3DX viewport)
    {
        _viewport = viewport;
        _viewport.Items.Add(_sceneRoot);
        _viewport.Items.Add(_edgeRoot);
        _viewport.Items.Add(_diagnosticsRoot);
        _viewport.Items.Add(_highlightRoot);
    }

    /// <summary>Exposed so selection mapping can be asserted without standing up a GPU device.</summary>
    public InstanceIdentityMap Identities => _identities;

    public bool ShowEdges
    {
        get => _showEdges;
        set
        {
            _showEdges = value;
            _edgeRoot.IsRendering = value;
        }
    }

    public bool ShowDiagnostics
    {
        get => _showDiagnostics;
        set
        {
            _showDiagnostics = value;
            _diagnosticsRoot.IsRendering = value;
            if (value && _diagnosticsRoot.Children.Count == 0 && _scene is not null)
            {
                BuildDiagnostics(_scene);
            }
        }
    }

    public RenderStatistics Load(RenderScene scene)
    {
        var watch = Stopwatch.StartNew();
        Clear();
        _scene = scene;

        // One GPU buffer per (part, mesh group), reused by every colour variant of that part.
        var buffers = new Dictionary<(string Part, int Group), HelixMesh>();

        foreach (var batch in scene.Batches)
        {
            var group = scene.Meshes[batch.CanonicalPartName].Groups[batch.MeshGroupIndex];
            if (group.TriangleCount == 0)
            {
                continue;
            }

            var key = (batch.CanonicalPartName, batch.MeshGroupIndex);
            if (!buffers.TryGetValue(key, out var geometry))
            {
                buffers[key] = geometry = ToHelixMesh(group);
            }

            _sceneRoot.Children.Add(CreateInstancedModel(scene, batch, geometry));
        }

        AddStaticGeometry(scene);

        if (ShowEdges)
        {
            AddEdges(scene);
        }

        if (ShowDiagnostics)
        {
            BuildDiagnostics(scene);
        }

        watch.Stop();

        return new RenderStatistics(
            scene.Instances.Length,
            _sceneRoot.Children.Count,
            buffers.Count,
            scene.UploadedTriangleCount,
            scene.TriangleCount,
            watch.ElapsedMilliseconds);
    }

    public void SetMechanicsDiagnostics(ConnectionAnalysis? analysis)
    {
        _mechanics = analysis;
        if (ShowDiagnostics && _scene is not null)
        {
            BuildDiagnostics(_scene);
        }
    }

    private InstancingMeshGeometryModel3D CreateInstancedModel(
        RenderScene scene, InstanceBatch batch, HelixMesh geometry)
    {
        var transforms = new List<Matrix4x4>(batch.InstanceCount);
        var instanceIds = ImmutableArray.CreateBuilder<string>(batch.InstanceCount);

        foreach (var index in batch.InstanceIndices)
        {
            var instance = scene.Instances[index];
            transforms.Add(LDrawAxes.TransformToRenderer(instance.Transform));
            instanceIds.Add(instance.InstanceId);
        }

        var model = new InstancingMeshGeometryModel3D
        {
            Geometry = geometry,
            Instances = transforms,
            Material = CreateMaterial(batch.Colour),
            CullMode = batch.DoubleSided
                ? SharpDX.Direct3D11.CullMode.None
                : SharpDX.Direct3D11.CullMode.Back,
            IsTransparent = batch.Colour.IsTranslucent,
        };

        // A hit reports the instance index within this model, so identity is recorded per model
        // in the same order the transforms were added.
        _identities.Register(model, instanceIds.MoveToImmutable());
        return model;
    }

    /// <summary>
    /// Adds the generated hose and spring fallback meshes. These are decorative and are drawn
    /// without instance identifiers, so a click passes through them rather than selecting
    /// something that is not a logical part.
    /// </summary>
    private void AddStaticGeometry(RenderScene scene)
    {
        foreach (var group in scene.StaticGeometry.Groups)
        {
            if (group.TriangleCount == 0)
            {
                continue;
            }

            _sceneRoot.Children.Add(new MeshGeometryModel3D
            {
                Geometry = ToHelixMesh(group),
                Material = CreateMaterial(ResolveStaticColour(group.ColourCode)),
                CullMode = group.DoubleSided
                    ? SharpDX.Direct3D11.CullMode.None
                    : SharpDX.Direct3D11.CullMode.Back,
                IsHitTestVisible = false,
            });
        }
    }

    private static ResolvedColour ResolveStaticColour(int code) =>
        ColourPalette.Fallback.Resolve(code, ColourPalette.Fallback.DefaultContext);

    /// <summary>
    /// Draws type-2 edges for the static geometry. Per-instance edges need an instanced line
    /// shader, which the solid pass the Phase 1 gate covers does not require.
    /// </summary>
    private void AddEdges(RenderScene scene)
    {
        foreach (var group in scene.StaticGeometry.EdgeGroups)
        {
            if (group.SegmentCount == 0)
            {
                continue;
            }

            var builder = new LineBuilder();
            for (var i = 0; i < group.Indices.Length; i += 2)
            {
                builder.AddLine(
                    ToVector3(LDrawAxes.PointToRenderer(group.Positions[group.Indices[i]])),
                    ToVector3(LDrawAxes.PointToRenderer(group.Positions[group.Indices[i + 1]])));
            }

            _edgeRoot.Children.Add(new LineGeometryModel3D
            {
                Geometry = builder.ToLineGeometry3D(),
                Color = Colors.Black,
                Thickness = 0.5,
                IsHitTestVisible = false,
            });
        }
    }

    /// <summary>
    /// Draws model axes and per-instance bounds. This exists so poses can be inspected before
    /// shadow features are extracted, when a wrongly composed transform would otherwise only
    /// show up as a vaguely wrong-looking model.
    /// </summary>
    private void BuildDiagnostics(RenderScene scene)
    {
        _diagnosticsRoot.Children.Clear();

        var size = scene.Bounds.IsEmpty ? 100f : scene.Bounds.Size.Length() * 0.25f;
        _diagnosticsRoot.Children.Add(BuildAxis(Vector3.UnitX * size, Colors.Red));
        _diagnosticsRoot.Children.Add(BuildAxis(Vector3.UnitY * size, Colors.Green));
        _diagnosticsRoot.Children.Add(BuildAxis(Vector3.UnitZ * size, Colors.Blue));

        var boxes = new LineBuilder();
        foreach (var instance in scene.Instances)
        {
            AddBox(boxes, instance.WorldBounds);
        }

        _diagnosticsRoot.Children.Add(new LineGeometryModel3D
        {
            Geometry = boxes.ToLineGeometry3D(),
            Color = MediaColor.FromArgb(90, 255, 200, 0),
            Thickness = 0.5,
            IsHitTestVisible = false,
        });

        if (_mechanics is not null)
        {
            BuildMechanicsDiagnostics(_mechanics);
        }
    }

    private void BuildMechanicsDiagnostics(ConnectionAnalysis analysis)
    {
        var matched = analysis.Connections
            .SelectMany(connection => new[] { connection.FeatureA, connection.FeatureB })
            .ToHashSet(StringComparer.Ordinal);
        var ambiguous = analysis.Ambiguities
            .Select(ambiguity => ambiguity.FeatureKey)
            .ToHashSet(StringComparer.Ordinal);
        var axes = new LineBuilder();
        var matchedSections = new LineBuilder();
        var unmatchedSections = new LineBuilder();
        var ambiguousSections = new LineBuilder();
        var mates = new LineBuilder();
        var ambiguousMates = new LineBuilder();

        foreach (var feature in analysis.Features)
        {
            var axis = FeatureGeometry.Axis(feature);
            axes.AddLine(ToVector3(LDrawAxes.PointToRenderer(axis.Start)), ToVector3(LDrawAxes.PointToRenderer(axis.End)));

            var sectionBuilder = ambiguous.Contains(feature.Key)
                ? ambiguousSections
                : matched.Contains(feature.Key)
                    ? matchedSections
                    : unmatchedSections;
            AddSectionOutlines(sectionBuilder, feature);
        }

        foreach (var connection in analysis.Connections)
        {
            var a = analysis.FindFeature(connection.FeatureA);
            var b = analysis.FindFeature(connection.FeatureB);
            if (a is null || b is null)
            {
                continue;
            }

            var centreA = FeatureGeometry.Axis(a).Centre;
            var centreB = FeatureGeometry.Axis(b).Centre;
            var builder = connection.IsAmbiguous ? ambiguousMates : mates;
            if (Vector3.DistanceSquared(centreA, centreB) > 1e-4f)
            {
                builder.AddLine(
                    ToVector3(LDrawAxes.PointToRenderer(centreA)),
                    ToVector3(LDrawAxes.PointToRenderer(centreB)));
            }
            else
            {
                // Coaxial mates often share a centre, so a short cross makes the edge visible.
                var axis = FeatureGeometry.Axis(a).Direction;
                var side = Vector3.Cross(axis, Math.Abs(axis.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX);
                side = side.LengthSquared() > 1e-8f ? Vector3.Normalize(side) * 3f : Vector3.UnitX * 3f;
                builder.AddLine(
                    ToVector3(LDrawAxes.PointToRenderer(centreA - side)),
                    ToVector3(LDrawAxes.PointToRenderer(centreA + side)));
            }
        }

        AddDiagnosticLines(axes, Colors.DeepSkyBlue, 0.7);
        AddDiagnosticLines(matchedSections, Colors.Cyan, 1.0);
        AddDiagnosticLines(unmatchedSections, Colors.Orange, 1.0);
        AddDiagnosticLines(ambiguousSections, Colors.Magenta, 1.3);
        AddDiagnosticLines(mates, Colors.LimeGreen, 2.0);
        AddDiagnosticLines(ambiguousMates, Colors.Magenta, 2.0);
    }

    private void AddDiagnosticLines(LineBuilder builder, MediaColor colour, double thickness)
    {
        var geometry = builder.ToLineGeometry3D();
        if (geometry.Positions is null || geometry.Positions.Count == 0)
        {
            return;
        }

        _diagnosticsRoot.Children.Add(new LineGeometryModel3D
        {
            Geometry = geometry,
            Color = colour,
            Thickness = thickness,
            IsHitTestVisible = false,
        });
    }

    private static void AddSectionOutlines(LineBuilder builder, PlacedFeature feature)
    {
        var sections = FeatureGeometry.CylinderSections(feature);
        if (sections.IsEmpty)
        {
            return;
        }

        var x = Vector3.TransformNormal(Vector3.UnitX, feature.WorldTransform);
        var z = Vector3.TransformNormal(Vector3.UnitZ, feature.WorldTransform);
        x = x.LengthSquared() > 1e-8f ? Vector3.Normalize(x) : Vector3.UnitX;
        z = z.LengthSquared() > 1e-8f ? Vector3.Normalize(z) : Vector3.UnitZ;

        AddCircle(builder, sections[0].Start, sections[0].Radius, x, z);
        foreach (var section in sections)
        {
            AddCircle(builder, section.End, section.Radius, x, z);
            builder.AddLine(
                ToVector3(LDrawAxes.PointToRenderer(section.Start + (x * section.Radius))),
                ToVector3(LDrawAxes.PointToRenderer(section.End + (x * section.Radius))));
            builder.AddLine(
                ToVector3(LDrawAxes.PointToRenderer(section.Start - (x * section.Radius))),
                ToVector3(LDrawAxes.PointToRenderer(section.End - (x * section.Radius))));
        }
    }

    private static void AddCircle(LineBuilder builder, Vector3 centre, float radius, Vector3 x, Vector3 z)
    {
        const int segments = 16;
        var previous = centre + (x * radius);
        for (var index = 1; index <= segments; index++)
        {
            var angle = index * MathF.Tau / segments;
            var current = centre + (x * MathF.Cos(angle) * radius) + (z * MathF.Sin(angle) * radius);
            builder.AddLine(
                ToVector3(LDrawAxes.PointToRenderer(previous)),
                ToVector3(LDrawAxes.PointToRenderer(current)));
            previous = current;
        }
    }

    private static LineGeometryModel3D BuildAxis(Vector3 direction, MediaColor colour)
    {
        var builder = new LineBuilder();
        builder.AddLine(
            ToVector3(LDrawAxes.PointToRenderer(Vector3.Zero)),
            ToVector3(LDrawAxes.PointToRenderer(direction)));

        return new LineGeometryModel3D
        {
            Geometry = builder.ToLineGeometry3D(),
            Color = colour,
            Thickness = 2,
            IsHitTestVisible = false,
        };
    }

    private static void AddBox(LineBuilder builder, Bounds bounds)
    {
        if (bounds.IsEmpty)
        {
            return;
        }

        var c = bounds.Corners().Select(p => ToVector3(LDrawAxes.PointToRenderer(p))).ToArray();

        // Corner order from Bounds.Corners is a bit-pattern over (x, y, z).
        int[,] edges =
        {
            { 0, 1 }, { 0, 2 }, { 0, 4 }, { 1, 3 }, { 1, 5 }, { 2, 3 },
            { 2, 6 }, { 3, 7 }, { 4, 5 }, { 4, 6 }, { 5, 7 }, { 6, 7 },
        };

        for (var i = 0; i < edges.GetLength(0); i++)
        {
            builder.AddLine(c[edges[i, 0]], c[edges[i, 1]]);
        }
    }

    public string? PickInstance(Point viewportPoint)
    {
        var hits = _viewport.FindHits(viewportPoint);
        PickDiagnostics.Log(viewportPoint, hits);

        // Hits arrive sorted nearest-first, so the first resolvable one is what was clicked.
        // Static hose and spring geometry is not hit-test visible and so never appears here.
        foreach (var hit in hits)
        {
            if (hit.IsValid
                && hit.Tag is int instanceIndex
                && _identities.TryResolve(hit.ModelHit, instanceIndex, out var instanceId))
            {
                return instanceId;
            }
        }

        return null;
    }

    public void Highlight(string? instanceId)
    {
        _highlightRoot.Children.Clear();

        if (instanceId is null || _scene is null)
        {
            return;
        }

        var instance = _scene.FindInstance(instanceId);
        if (instance is null || instance.WorldBounds.IsEmpty)
        {
            return;
        }

        var builder = new LineBuilder();
        AddBox(builder, instance.WorldBounds);

        _highlightRoot.Children.Add(new LineGeometryModel3D
        {
            Geometry = builder.ToLineGeometry3D(),
            Color = Colors.Cyan,
            Thickness = 2,
            IsHitTestVisible = false,
        });
    }

    public void ZoomToFit()
    {
        if (_scene is null || _scene.Bounds.IsEmpty)
        {
            return;
        }

        FrameBounds(_scene.Bounds);
    }

    public void ZoomToInstance(string instanceId)
    {
        var instance = _scene?.FindInstance(instanceId);
        if (instance is not null && !instance.WorldBounds.IsEmpty)
        {
            FrameBounds(instance.WorldBounds);
        }
    }

    private void FrameBounds(Bounds bounds)
    {
        var centre = ToVector3(LDrawAxes.PointToRenderer(bounds.Centre));
        var radius = Math.Max(bounds.Size.Length() * 0.5f, 1f);

        if (_viewport.Camera is not PerspectiveCamera camera)
        {
            return;
        }

        // Pull back along a fixed three-quarter view so framing is repeatable.
        var direction = Vector3.Normalize(new Vector3(-1f, -0.6f, -1f));
        var distance = radius * 2.6f;
        var position = centre - (direction * distance);

        camera.Position = new System.Windows.Media.Media3D.Point3D(position.X, position.Y, position.Z);
        camera.LookDirection = new System.Windows.Media.Media3D.Vector3D(
            direction.X * distance, direction.Y * distance, direction.Z * distance);
        camera.UpDirection = new System.Windows.Media.Media3D.Vector3D(0, 1, 0);
        camera.FarPlaneDistance = distance * 10;
        camera.NearPlaneDistance = Math.Max(distance / 1000, 0.1);
    }

    public void Clear()
    {
        _sceneRoot.Children.Clear();
        _edgeRoot.Children.Clear();
        _diagnosticsRoot.Children.Clear();
        _highlightRoot.Children.Clear();
        _identities.Clear();
        _scene = null;
        _mechanics = null;
    }

    private static HelixMesh ToHelixMesh(MeshGroup group)
    {
        var positions = new Vector3Collection(group.Positions.Length);
        var normals = new Vector3Collection(group.Normals.Length);

        foreach (var position in group.Positions)
        {
            positions.Add(ToVector3(position));
        }

        foreach (var normal in group.Normals)
        {
            normals.Add(ToVector3(normal));
        }

        return new HelixMesh
        {
            Positions = positions,
            Normals = normals,
            Indices = new IntCollection(group.Indices),
        };
    }

    private static PhongMaterial CreateMaterial(ResolvedColour colour) => new()
    {
        DiffuseColor = ToColor4(colour.Value),
        SpecularColor = new Color4(0.18f, 0.18f, 0.18f, 1f),
        SpecularShininess = 24f,
        AmbientColor = ToColor4(Scale(colour.Value, 0.35f)),
    };

    private static Colour Scale(Colour colour, float factor) => new(
        (byte)(colour.R * factor), (byte)(colour.G * factor), (byte)(colour.B * factor), colour.A);

    private static Color4 ToColor4(Colour colour) =>
        new(colour.R / 255f, colour.G / 255f, colour.B / 255f, colour.A / 255f);

    /// <summary>
    /// The geometry pipeline works in <see cref="System.Numerics"/>; Helix's collections want
    /// its own vector type. They are layout-identical, so this is a field copy, not a change of
    /// coordinate system -- that already happened in <see cref="LDrawAxes"/>.
    /// </summary>
    private static Vector3 ToVector3(Vector3 v) => v;
}

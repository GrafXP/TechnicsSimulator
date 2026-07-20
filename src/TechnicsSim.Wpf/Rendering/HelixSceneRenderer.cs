using System.Collections.Immutable;
using System.Diagnostics;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
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
    /// <summary>Colour of the selected instance's solid overlay and its bounding box.</summary>
    private static readonly MediaColor SelectionColour = MediaColor.FromRgb(0, 230, 255);

    private readonly Viewport3DX _viewport;
    private readonly GroupModel3D _sceneRoot = new();
    private readonly GroupModel3D _edgeRoot = new();
    private readonly GroupModel3D _diagnosticsRoot = new();
    private readonly GroupModel3D _highlightRoot = new();
    private readonly InstanceIdentityMap _identities = new();

    // One GPU buffer per (part, mesh group), reused by every colour variant of that part and
    // kept across rebuilds so toggling emphasis re-uploads instance transforms, not geometry.
    private readonly Dictionary<(string Part, int Group), HelixMesh> _buffers = [];
    private readonly HashSet<string> _mechanical = new(StringComparer.Ordinal);
    private readonly HashSet<string> _highlighted = new(StringComparer.Ordinal);
    private readonly List<(PhongMaterial Material, ResolvedColour Colour)> _ghostMaterials = [];
    private readonly Dictionary<string, SceneInstance> _instancesById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Matrix4x4> _animatedTransforms = new(StringComparer.Ordinal);
    private readonly List<InstanceModelBinding> _sceneModelBindings = [];
    private readonly List<InstanceModelBinding> _highlightModelBindings = [];
    private LineGeometryModel3D? _highlightBoundsModel;

    private RenderScene? _scene;
    private ConnectionAnalysis? _mechanics;
    private bool _showEdges;
    private bool _showDiagnostics;
    private bool _emphasizeMechanics;
    private double _ghostOpacity = 0.2;

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

    public bool EmphasizeMechanics
    {
        get => _emphasizeMechanics;
        set
        {
            if (_emphasizeMechanics == value)
            {
                return;
            }

            _emphasizeMechanics = value;
            RebuildInstanceModels();
        }
    }

    public double GhostOpacity
    {
        get => _ghostOpacity;
        set
        {
            var clamped = Math.Clamp(value, 0.02, 1.0);
            if (Math.Abs(_ghostOpacity - clamped) < 0.001)
            {
                return;
            }

            _ghostOpacity = clamped;
            if (_emphasizeMechanics)
            {
                // Opacity is a material property. Rebuilding hundreds of scene elements for
                // every pixel the slider moves over made the control unnecessarily expensive.
                foreach (var (material, colour) in _ghostMaterials)
                {
                    material.DiffuseColor = GhostDiffuse(colour);
                }

                RequestRedraw();
            }
        }
    }

    public RenderStatistics Load(RenderScene scene)
    {
        var watch = Stopwatch.StartNew();
        Clear();
        _scene = scene;

        foreach (var instance in scene.Instances)
        {
            _instancesById[instance.InstanceId] = instance;
        }

        foreach (var batch in scene.Batches)
        {
            var group = scene.Meshes[batch.CanonicalPartName].Groups[batch.MeshGroupIndex];
            var key = (batch.CanonicalPartName, batch.MeshGroupIndex);
            if (group.TriangleCount > 0 && !_buffers.ContainsKey(key))
            {
                _buffers[key] = ToHelixMesh(group);
            }
        }

        RebuildInstanceModels();

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
            _buffers.Count,
            scene.UploadedTriangleCount,
            scene.TriangleCount,
            watch.ElapsedMilliseconds);
    }

    public void SetMechanicalInstances(IEnumerable<string> instanceIds)
    {
        _mechanical.Clear();
        foreach (var id in instanceIds)
        {
            _mechanical.Add(id);
        }

        if (_emphasizeMechanics)
        {
            RebuildInstanceModels();
        }
    }

    /// <summary>
    /// Rebuilds the solid pass, splitting every batch into the instances drawn normally and the
    /// instances drawn faded.
    ///
    /// The split has to happen at batch level because one instanced model carries one material,
    /// so "fade everything except the drivetrain" is not a material setting but a different draw.
    /// The geometry buffers are shared across the split and across rebuilds, so the cost is
    /// re-uploading instance transforms -- a few hundred kilobytes for the largest model.
    ///
    /// Highlighted instances are left out of this pass entirely and drawn by
    /// <see cref="RebuildHighlight"/> instead. Drawing a bright copy on top of the original would
    /// put two coincident surfaces in the depth buffer and flicker.
    /// </summary>
    private void RebuildInstanceModels()
    {
        RemoveAll(_sceneRoot);
        _identities.Clear();
        _ghostMaterials.Clear();
        _sceneModelBindings.Clear();

        if (_scene is null)
        {
            return;
        }

        var solid = new List<int>();
        var ghost = new List<int>();

        foreach (var batch in _scene.Batches)
        {
            if (!_buffers.TryGetValue((batch.CanonicalPartName, batch.MeshGroupIndex), out var geometry))
            {
                continue;
            }

            solid.Clear();
            ghost.Clear();

            foreach (var index in batch.InstanceIndices)
            {
                var id = _scene.Instances[index].InstanceId;
                if (_highlighted.Contains(id))
                {
                    continue;
                }

                (IsFaded(id) ? ghost : solid).Add(index);
            }

            if (solid.Count > 0)
            {
                _sceneRoot.Children.Add(CreateInstancedModel(_scene, batch, geometry, solid, faded: false));
            }

            if (ghost.Count > 0)
            {
                _sceneRoot.Children.Add(CreateInstancedModel(_scene, batch, geometry, ghost, faded: true));
            }
        }

        AddStaticGeometry(_scene);
        RebuildHighlight();
        RequestRedraw();
    }

    /// <summary>
    /// Forces the viewport to redraw after its child models were replaced.
    ///
    /// The renderer iterates a flattened per-frame list rather than the scene graph directly.
    /// <c>InvalidatePerFrameRenderables</c> rebuilds that list after scene elements change and
    /// also wakes Helix's on-demand render loop.
    /// </summary>
    private void RequestRedraw() => _viewport.RenderHost?.InvalidatePerFrameRenderables();

    /// <summary>
    /// Empties a group one child at a time.
    ///
    /// <c>Children.Clear()</c> raises a single <c>Reset</c> that carries no <c>OldItems</c>, and
    /// the group cannot detach scene nodes it was never handed, so the replacements never reach
    /// the screen. Individual removals carry the item being removed and detach correctly.
    ///
    /// This is a separate defect from the missing render invalidation in
    /// <see cref="RebuildInstanceModels"/>, and both have to be fixed: swapping this back to
    /// <c>Clear()</c> reproduces a stale viewport even with the invalidation in place. That was
    /// checked, not assumed.
    /// </summary>
    private static void RemoveAll(GroupModel3D group)
    {
        for (var index = group.Children.Count - 1; index >= 0; index--)
        {
            group.Children.RemoveAt(index);
        }
    }

    /// <summary>The drivetrain is never faded; everything else is, once emphasis is on.</summary>
    private bool IsFaded(string instanceId) =>
        _emphasizeMechanics && !_mechanical.Contains(instanceId);

    public void SetMechanicsDiagnostics(ConnectionAnalysis? analysis)
    {
        _mechanics = analysis;
        if (ShowDiagnostics && _scene is not null)
        {
            BuildDiagnostics(_scene);
        }
    }

    private InstancingMeshGeometryModel3D CreateInstancedModel(
        RenderScene scene, InstanceBatch batch, HelixMesh geometry, List<int> indices, bool faded)
    {
        var instanceIds = ImmutableArray.CreateBuilder<string>(indices.Count);

        foreach (var index in indices)
        {
            var instance = scene.Instances[index];
            instanceIds.Add(instance.InstanceId);
        }

        var ids = instanceIds.MoveToImmutable();

        var model = new InstancingMeshGeometryModel3D
        {
            Geometry = geometry,
            Instances = TransformsFor(ids),
            Material = faded ? CreateGhostMaterial(batch.Colour) : CreateMaterial(batch.Colour),
            CullMode = batch.DoubleSided
                ? SharpDX.Direct3D11.CullMode.None
                : SharpDX.Direct3D11.CullMode.Back,
            IsTransparent = faded || batch.Colour.IsTranslucent,

            // Faded geometry is context, not a target: a click passes through the bodywork to the
            // drivetrain part behind it, which is the whole point of turning emphasis on.
            IsHitTestVisible = !faded,
        };

        // A hit reports the instance index within this model, so identity is recorded per model
        // in the same order the transforms were added.
        _identities.Register(model, ids);
        _sceneModelBindings.Add(new InstanceModelBinding(model, ids));
        return model;
    }

    /// <summary>
    /// Adds the generated hose and spring fallback meshes. These are decorative and are drawn
    /// without instance identifiers, so a click passes through them rather than selecting
    /// something that is not a logical part.
    ///
    /// They are never logical parts, so they are never part of the drivetrain and fade with the
    /// rest of the context.
    /// </summary>
    private void AddStaticGeometry(RenderScene scene)
    {
        foreach (var group in scene.StaticGeometry.Groups)
        {
            if (group.TriangleCount == 0)
            {
                continue;
            }

            var colour = ResolveStaticColour(group.ColourCode);
            _sceneRoot.Children.Add(new MeshGeometryModel3D
            {
                Geometry = ToHelixMesh(group),
                Material = _emphasizeMechanics ? CreateGhostMaterial(colour) : CreateMaterial(colour),
                CullMode = group.DoubleSided
                    ? SharpDX.Direct3D11.CullMode.None
                    : SharpDX.Direct3D11.CullMode.Back,
                IsTransparent = _emphasizeMechanics || colour.IsTranslucent,
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
        RemoveAll(_diagnosticsRoot);

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

    public void Highlight(IReadOnlyCollection<string> instanceIds)
    {
        if (_highlighted.Count == instanceIds.Count && instanceIds.All(_highlighted.Contains))
        {
            return;
        }

        _highlighted.Clear();
        foreach (var id in instanceIds)
        {
            _highlighted.Add(id);
        }

        // The solid pass decides which instances it skips, so a changed selection means it has
        // to be rebuilt rather than merely overlaid.
        RebuildInstanceModels();
    }

    /// <summary>
    /// Draws the selected instances as a solid bright copy of their own geometry, plus a
    /// bounding box.
    ///
    /// The box alone was the previous highlight and is not enough: on a model the size of 42100
    /// a 24-tooth gear's outline is a few pixels of thin line among thousands of parts. Repainting
    /// the part itself is what makes it findable; the box stays because it is still what locates
    /// a part that is currently hidden behind something else.
    /// </summary>
    private void RebuildHighlight()
    {
        RemoveAll(_highlightRoot);
        _highlightModelBindings.Clear();
        _highlightBoundsModel = null;

        if (_scene is null || _highlighted.Count == 0)
        {
            return;
        }

        var boxes = new LineBuilder();
        var boxed = false;

        foreach (var id in _highlighted)
        {
            var instance = _scene.FindInstance(id);
            if (instance is null)
            {
                continue;
            }

            var bounds = AnimatedBoundsFor(instance);
            if (!bounds.IsEmpty)
            {
                AddBox(boxes, bounds);
                boxed = true;
            }

            if (!_scene.Meshes.TryGetValue(instance.CanonicalPartName, out var mesh))
            {
                continue;
            }

            for (var group = 0; group < mesh.Groups.Length; group++)
            {
                if (!_buffers.TryGetValue((instance.CanonicalPartName, group), out var geometry))
                {
                    continue;
                }

                var model = new InstancingMeshGeometryModel3D
                {
                    Geometry = geometry,
                    Instances = TransformsFor([id]),
                    Material = HighlightMaterial,
                    CullMode = mesh.Groups[group].DoubleSided
                        ? SharpDX.Direct3D11.CullMode.None
                        : SharpDX.Direct3D11.CullMode.Back,
                };

                // Registered so that clicking an already-highlighted part re-selects it instead
                // of picking whatever this overlay is standing in front of.
                _identities.Register(model, [id]);
                _highlightModelBindings.Add(new InstanceModelBinding(model, [id]));
                _highlightRoot.Children.Add(model);
            }
        }

        if (boxed)
        {
            _highlightBoundsModel = new LineGeometryModel3D
            {
                Geometry = boxes.ToLineGeometry3D(),
                Color = SelectionColour,
                Thickness = 2,
                IsHitTestVisible = false,
            };
            _highlightRoot.Children.Add(_highlightBoundsModel);
        }
    }

    public void ZoomToFit()
    {
        if (_scene is null || _scene.Bounds.IsEmpty)
        {
            return;
        }

        FrameBounds(_scene.Bounds);
    }

    public void ZoomToInstances(IReadOnlyCollection<string> instanceIds)
    {
        if (_scene is null)
        {
            return;
        }

        var bounds = Bounds.Empty;
        foreach (var instanceId in instanceIds)
        {
            var instance = _scene.FindInstance(instanceId);
            if (instance is not null)
            {
                bounds = bounds.Union(instance.WorldBounds);
            }
        }

        if (!bounds.IsEmpty)
        {
            FrameBounds(bounds);
        }
    }

    public void SetInstanceTransforms(IReadOnlyDictionary<string, Matrix4x4> transforms)
    {
        var affected = _animatedTransforms.Keys
            .Concat(transforms.Keys)
            .ToHashSet(StringComparer.Ordinal);

        _animatedTransforms.Clear();
        foreach (var (instanceId, transform) in transforms)
        {
            if (_instancesById.ContainsKey(instanceId))
            {
                _animatedTransforms[instanceId] = transform;
            }
        }

        UpdateTransforms(_sceneModelBindings, affected);
        UpdateTransforms(_highlightModelBindings, affected);
        if (_highlightBoundsModel is not null && _highlighted.Any(affected.Contains))
        {
            UpdateHighlightBounds();
        }

        RequestRedraw();
    }

    private void FrameBounds(Bounds bounds)
    {
        var transformed = bounds.Corners()
            .Select(LDrawAxes.PointToRenderer)
            .ToArray();
        var min = transformed.Aggregate(Vector3.Min);
        var max = transformed.Aggregate(Vector3.Max);

        // Helix accounts for field of view and the viewport's actual aspect ratio here. The old
        // guessed distance was unreliable for long, narrow gear/shaft combinations.
        var rect = new Rect3D(
            min.X, min.Y, min.Z,
            Math.Max(max.X - min.X, 1f),
            Math.Max(max.Y - min.Y, 1f),
            Math.Max(max.Z - min.Z, 1f));
        _viewport.ZoomExtents(rect, 200);
    }

    public void Clear()
    {
        RemoveAll(_sceneRoot);
        RemoveAll(_edgeRoot);
        RemoveAll(_diagnosticsRoot);
        RemoveAll(_highlightRoot);
        _identities.Clear();
        _buffers.Clear();
        _mechanical.Clear();
        _highlighted.Clear();
        _ghostMaterials.Clear();
        _instancesById.Clear();
        _animatedTransforms.Clear();
        _sceneModelBindings.Clear();
        _highlightModelBindings.Clear();
        _highlightBoundsModel = null;
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

    /// <summary>
    /// The material for faded context geometry.
    ///
    /// It desaturates as well as fading. Alpha alone leaves a red panel reading as pink and still
    /// dominating the frame, which defeats the purpose; pulling colour towards neutral grey makes
    /// the remaining solid parts the only saturated things on screen.
    /// </summary>
    private PhongMaterial CreateGhostMaterial(ResolvedColour colour)
    {
        var faded = Desaturate(colour.Value, 0.75f);
        var material = new PhongMaterial
        {
            DiffuseColor = GhostDiffuse(colour),
            SpecularColor = new Color4(0.02f, 0.02f, 0.02f, 1f),
            SpecularShininess = 1f,
            AmbientColor = ToColor4(Scale(faded, 0.25f)),
        };

        _ghostMaterials.Add((material, colour));
        return material;
    }

    private Color4 GhostDiffuse(ResolvedColour colour)
    {
        var faded = Desaturate(colour.Value, 0.75f);
        var alpha = (float)_ghostOpacity * (colour.Value.A / 255f);
        return new Color4(faded.R / 255f, faded.G / 255f, faded.B / 255f, alpha);
    }

    /// <summary>Self-lit so the selection reads the same from every angle, including unlit faces.</summary>
    private static PhongMaterial HighlightMaterial => new()
    {
        DiffuseColor = new Color4(0.10f, 0.62f, 0.72f, 1f),
        EmissiveColor = new Color4(0.00f, 0.45f, 0.55f, 1f),
        SpecularColor = new Color4(0.60f, 0.90f, 1.00f, 1f),
        SpecularShininess = 40f,
        AmbientColor = new Color4(0.05f, 0.25f, 0.30f, 1f),
    };

    private static Colour Scale(Colour colour, float factor) => new(
        (byte)(colour.R * factor), (byte)(colour.G * factor), (byte)(colour.B * factor), colour.A);

    /// <summary>Pulls a colour towards its own luminance, so it greys out without changing brightness.</summary>
    private static Colour Desaturate(Colour colour, float amount)
    {
        var grey = (colour.R * 0.30f) + (colour.G * 0.59f) + (colour.B * 0.11f);
        return new Colour(
            (byte)(colour.R + ((grey - colour.R) * amount)),
            (byte)(colour.G + ((grey - colour.G) * amount)),
            (byte)(colour.B + ((grey - colour.B) * amount)),
            colour.A);
    }

    private static Color4 ToColor4(Colour colour) =>
        new(colour.R / 255f, colour.G / 255f, colour.B / 255f, colour.A / 255f);

    private List<Matrix4x4> TransformsFor(ImmutableArray<string> instanceIds)
    {
        var transforms = new List<Matrix4x4>(instanceIds.Length);
        foreach (var instanceId in instanceIds)
        {
            if (_instancesById.TryGetValue(instanceId, out var instance))
            {
                var transform = _animatedTransforms.GetValueOrDefault(instanceId, instance.Transform);
                transforms.Add(LDrawAxes.TransformToRenderer(transform));
            }
        }

        return transforms;
    }

    private void UpdateTransforms(
        IEnumerable<InstanceModelBinding> bindings,
        IReadOnlySet<string> affectedInstanceIds)
    {
        foreach (var binding in bindings)
        {
            if (binding.InstanceIds.Any(affectedInstanceIds.Contains))
            {
                binding.Model.Instances = TransformsFor(binding.InstanceIds);
            }
        }
    }

    private void UpdateHighlightBounds()
    {
        if (_scene is null || _highlightBoundsModel is null)
        {
            return;
        }

        var boxes = new LineBuilder();
        foreach (var instanceId in _highlighted)
        {
            if (_instancesById.TryGetValue(instanceId, out var instance))
            {
                AddBox(boxes, AnimatedBoundsFor(instance));
            }
        }

        _highlightBoundsModel.Geometry = boxes.ToLineGeometry3D();
    }

    private Bounds AnimatedBoundsFor(SceneInstance instance)
    {
        if (!_animatedTransforms.TryGetValue(instance.InstanceId, out var transform)
            || _scene is null
            || !_scene.Meshes.TryGetValue(instance.CanonicalPartName, out var mesh)
            || mesh.Bounds.IsEmpty)
        {
            return instance.WorldBounds;
        }

        var result = Bounds.Empty;
        foreach (var corner in mesh.Bounds.Corners())
        {
            result = result.Include(Vector3.Transform(corner, transform));
        }

        return result;
    }

    private sealed record InstanceModelBinding(
        InstancingMeshGeometryModel3D Model,
        ImmutableArray<string> InstanceIds);

    /// <summary>
    /// The geometry pipeline works in <see cref="System.Numerics"/>; Helix's collections want
    /// its own vector type. They are layout-identical, so this is a field copy, not a change of
    /// coordinate system -- that already happened in <see cref="LDrawAxes"/>.
    /// </summary>
    private static Vector3 ToVector3(Vector3 v) => v;
}

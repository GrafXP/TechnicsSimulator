using System.Numerics;
using System.Windows;
using TechnicsSim.LDraw.Geometry;
using TechnicsSim.Mechanics.Mating;

namespace TechnicsSim.Wpf.Rendering;

/// <summary>What the renderer reports back about the scene it built.</summary>
public sealed record RenderStatistics(
    int Instances,
    int InstancedModels,
    int DistinctVertexBuffers,
    int UploadedTriangles,
    int DrawnTriangles,
    long BuildMilliseconds);

/// <summary>
/// The viewer's view of a renderer. HelixToolkit types stay behind this boundary so the view
/// model, the mechanism code, and the tests never take a dependency on the toolkit, and so the
/// toolkit can be replaced without touching anything above it.
/// </summary>
public interface ISceneRenderer
{
    /// <summary>Uploads a scene, replacing whatever was loaded before.</summary>
    RenderStatistics Load(RenderScene scene);

    /// <summary>Supplies effective features and mate diagnostics for the overlay pass.</summary>
    void SetMechanicsDiagnostics(ConnectionAnalysis? analysis);

    void Clear();

    /// <summary>
    /// Resolves a viewport point to the logical instance ID under it, or null when the click
    /// missed everything or hit non-selectable static geometry.
    /// </summary>
    string? PickInstance(Point viewportPoint);

    /// <summary>
    /// Highlights a set of instances, or clears the highlight when passed an empty set.
    ///
    /// This takes a set rather than one ID because the mechanics panel's central row is a gear
    /// <i>mesh</i>: showing one of the two gears leaves the reviewer guessing which partner the
    /// row is about.
    /// </summary>
    void Highlight(IReadOnlyCollection<string> instanceIds);

    /// <summary>
    /// Records which instances the drivetrain graph accounts for, so
    /// <see cref="EmphasizeMechanics"/> knows what to keep solid.
    /// </summary>
    void SetMechanicalInstances(IEnumerable<string> instanceIds);

    /// <summary>
    /// Applies replacement model-space transforms to logical instances. Missing instances retain
    /// their loaded pose; an empty map restores the complete model to that pose.
    /// </summary>
    void SetInstanceTransforms(IReadOnlyDictionary<string, Matrix4x4> transforms);

    /// <summary>
    /// Fades every instance that is not part of the drivetrain down to <see cref="GhostOpacity"/>.
    ///
    /// Without this a gear is a few hundred triangles buried inside several thousand parts of
    /// bodywork, and no amount of outlining makes it findable.
    /// </summary>
    bool EmphasizeMechanics { get; set; }

    /// <summary>Opacity kept by faded context geometry, from nearly invisible to fully solid.</summary>
    double GhostOpacity { get; set; }

    /// <summary>Frames the camera on the whole model.</summary>
    void ZoomToFit();

    /// <summary>Frames the camera on the combined bounds of the supplied instances.</summary>
    void ZoomToInstances(IReadOnlyCollection<string> instanceIds);

    /// <summary>Draws type-2 edge lines as an additional pass.</summary>
    bool ShowEdges { get; set; }

    /// <summary>
    /// Draws model axes and per-instance bounding boxes. Useful before shadow extraction
    /// exists, when there is nothing else to look at but pose.
    /// </summary>
    bool ShowDiagnostics { get; set; }
}

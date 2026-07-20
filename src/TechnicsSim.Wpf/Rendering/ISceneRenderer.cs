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

    /// <summary>Highlights an instance, or clears the highlight when passed null.</summary>
    void Highlight(string? instanceId);

    /// <summary>Frames the camera on the whole model.</summary>
    void ZoomToFit();

    /// <summary>Frames the camera on one instance.</summary>
    void ZoomToInstance(string instanceId);

    /// <summary>Draws type-2 edge lines as an additional pass.</summary>
    bool ShowEdges { get; set; }

    /// <summary>
    /// Draws model axes and per-instance bounding boxes. Useful before shadow extraction
    /// exists, when there is nothing else to look at but pose.
    /// </summary>
    bool ShowDiagnostics { get; set; }
}

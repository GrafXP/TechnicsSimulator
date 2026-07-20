using System.IO;
using System.Windows;
using HelixToolkit.SharpDX;

namespace TechnicsSim.Wpf.Rendering;

/// <summary>
/// Optional tracing for picking, enabled by setting <c>TECHNICSSIM_PICK_LOG</c> to a file path.
///
/// Instanced hit-testing behaviour is the one part of the toolkit contract that reflection over
/// the assembly could not settle, so this exists to answer it from a running renderer rather
/// than by assumption.
/// </summary>
internal static class PickDiagnostics
{
    private static readonly string? LogPath = Environment.GetEnvironmentVariable("TECHNICSSIM_PICK_LOG");

    public static bool Enabled => !string.IsNullOrEmpty(LogPath);

    public static void Log(Point point, IList<HitTestResult> hits)
    {
        if (!Enabled)
        {
            return;
        }

        var lines = new List<string> { $"pick at {point.X:F0},{point.Y:F0} -> {hits.Count} hit(s)" };

        foreach (var hit in hits)
        {
            lines.Add(
                $"  valid={hit.IsValid} model={hit.ModelHit?.GetType().Name ?? "null"} "
                + $"tag={hit.Tag?.GetType().Name ?? "null"}:{hit.Tag} "
                + $"dist={hit.Distance:F1} point={hit.PointHit}");
        }

        try
        {
            File.AppendAllLines(LogPath!, lines);
        }
        catch (IOException)
        {
            // Diagnostics must never take the viewer down.
        }
    }
}

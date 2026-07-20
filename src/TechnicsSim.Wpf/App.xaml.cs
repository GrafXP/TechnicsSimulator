using System.Windows;

namespace TechnicsSim.Wpf;

/// <summary>
/// Application entry point.
///
/// Startup options exist so the viewer can be driven reproducibly for QA and the manual render
/// checklist, rather than depending on someone clicking the right controls in the right order:
///
/// <code>
/// TechnicsSim.Wpf.exe --model "Models/8275-1.mpd" --diagnostics --edges
/// </code>
/// </summary>
public partial class App : Application
{
    /// <summary>Model to open on startup; null opens the first available model.</summary>
    public static string? StartupModel { get; private set; }

    public static bool StartWithDiagnostics { get; private set; }

    public static bool StartWithEdges { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        for (var i = 0; i < e.Args.Length; i++)
        {
            switch (e.Args[i])
            {
                case "--model" when i + 1 < e.Args.Length:
                    StartupModel = e.Args[++i];
                    break;

                case "--diagnostics":
                    StartWithDiagnostics = true;
                    break;

                case "--edges":
                    StartWithEdges = true;
                    break;
            }
        }

        base.OnStartup(e);
    }
}

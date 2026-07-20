using TechnicsSim.LDraw.Library;

namespace TechnicsSim.Cli.Commands;

/// <summary>
/// Prints which external data sources are in use. Every reproducibility question starts here:
/// a coverage number is meaningless without the library revision that produced it.
/// </summary>
public static class LibraryInfoCommand
{
    public static int Run(Workspace workspace)
    {
        Console.WriteLine($"Repository root : {workspace.Root}");
        Console.WriteLine();

        Console.WriteLine("Official LDraw library");
        if (workspace.LibraryInfo is { } info)
        {
            Console.WriteLine($"  Source       : {info.Kind}");
            Console.WriteLine($"  Path         : {info.Path}");
            Console.WriteLine($"  Chosen by    : {info.ResolvedBy}");
            Console.WriteLine($"  Update tag   : {info.UpdateTag ?? "(unknown)"}");
            Console.WriteLine($"  Files        : {info.FileCount:N0}");

            if (info.SizeBytes > 0)
            {
                Console.WriteLine($"  Size         : {info.SizeBytes / 1024.0 / 1024.0:N1} MiB");
            }

            if (info.Sha256 is not null)
            {
                Console.WriteLine($"  SHA-256      : {info.Sha256}");
            }
        }
        else
        {
            Console.WriteLine("  NOT FOUND. Searched, in order:");
            foreach (var (path, reason) in LibraryLocator.EnumerateCandidates(null, workspace.Root))
            {
                if (path is not null)
                {
                    Console.WriteLine($"    - {reason}: {path}");
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine("LDCad shadow library");
        if (workspace.ShadowInfo is { } shadow)
        {
            Console.WriteLine($"  Path         : {shadow.Path}");
            Console.WriteLine($"  Commit       : {shadow.GitCommit ?? "(unknown)"}");
            Console.WriteLine($"  Part files   : {shadow.PartFileCount:N0}");
            Console.WriteLine($"  Prim files   : {shadow.PrimitiveFileCount:N0}");
            Console.WriteLine("  License      : CC BY-SA 4.0 (attribution required when redistributing)");
        }
        else
        {
            Console.WriteLine("  NOT FOUND. Run scripts/bootstrap-libraries.ps1.");
        }

        return workspace.LibraryInfo is null ? 1 : 0;
    }
}

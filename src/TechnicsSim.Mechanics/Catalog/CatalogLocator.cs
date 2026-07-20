namespace TechnicsSim.Mechanics.Catalog;

/// <summary>
/// Finds <c>data/parts-mechanics.json</c>.
///
/// The search order mirrors the official-library locator so the CLI, the tests, and a published
/// build all resolve the same file without per-host configuration: an explicit path wins, then
/// the data folder beside the running binary, then the repository checkout.
/// </summary>
public static class CatalogLocator
{
    public const string FileName = "parts-mechanics.json";

    public sealed record LocatedCatalog(string Path, string ChosenBy);

    public static LocatedCatalog? Locate(string? explicitPath, string? repositoryRoot = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            // An explicit path that does not exist is an error, not a reason to fall through to
            // a different catalog: silently loading other data would change every ratio.
            return File.Exists(explicitPath)
                ? new LocatedCatalog(System.IO.Path.GetFullPath(explicitPath), "explicit path")
                : null;
        }

        var beside = System.IO.Path.Combine(AppContext.BaseDirectory, "data", FileName);
        if (File.Exists(beside))
        {
            return new LocatedCatalog(beside, "data folder beside the binary");
        }

        var root = repositoryRoot ?? FindRepositoryRoot();
        if (root is not null)
        {
            var inRepository = System.IO.Path.Combine(root, "data", FileName);
            if (File.Exists(inRepository))
            {
                return new LocatedCatalog(inRepository, "repository data folder");
            }
        }

        return null;
    }

    public static MechanicsCatalog Load(string? explicitPath, string? repositoryRoot = null)
    {
        var located = Locate(explicitPath, repositoryRoot)
            ?? throw new FileNotFoundException(
                $"Could not find {FileName}. Pass --catalog, or run from a checkout containing data/{FileName}.");

        return CatalogLoader.LoadFile(located.Path);
    }

    private static string? FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(System.IO.Path.Combine(directory.FullName, ".git"))
                || File.Exists(System.IO.Path.Combine(directory.FullName, "TechnicsSimulator.slnx"))
                || File.Exists(System.IO.Path.Combine(directory.FullName, "TechnicsSimulator.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}

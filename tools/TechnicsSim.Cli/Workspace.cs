using TechnicsSim.LDraw.Library;
using TechnicsSim.LDraw.Sources;

namespace TechnicsSim.Cli;

/// <summary>
/// Resolves the repository root and opens the configured external data sources. Every command
/// goes through here so that all reports name the same, explicitly chosen inputs.
/// </summary>
public sealed class Workspace : IDisposable
{
    private Workspace(
        string root,
        ILDrawFileSource? library,
        LibraryInfo? libraryInfo,
        ILDrawFileSource? shadow,
        ShadowLibraryInfo? shadowInfo)
    {
        Root = root;
        Library = library;
        LibraryInfo = libraryInfo;
        Shadow = shadow;
        ShadowInfo = shadowInfo;
    }

    public string Root { get; }

    public ILDrawFileSource? Library { get; }

    public LibraryInfo? LibraryInfo { get; }

    public ILDrawFileSource? Shadow { get; }

    public ShadowLibraryInfo? ShadowInfo { get; }

    public static Workspace Open(CommandLine commandLine)
    {
        var root = FindRepositoryRoot();

        var located = LibraryLocator.Locate(commandLine.Option("ldraw"), root);
        var shadowInfo = LibraryLocator.LocateShadow(commandLine.Option("shadow"), root);
        var shadowSource = shadowInfo is null
            ? null
            : new DirectoryFileSource(shadowInfo.Path, "LDCad shadow library");

        return new Workspace(root, located?.Source, located?.Info, shadowSource, shadowInfo);
    }

    /// <summary>The official library, or a clear error explaining the configuration order.</summary>
    public ILDrawFileSource RequireLibrary()
    {
        if (Library is not null)
        {
            return Library;
        }

        var candidates = LibraryLocator.EnumerateCandidates(null, Root)
            .Where(c => c.Path is not null)
            .Select(c => $"    {c.Reason}: {c.Path}");

        throw new CommandLineException(
            "No official LDraw library found. Run scripts/bootstrap-libraries.ps1, or pass "
            + $"--ldraw <path>.{Environment.NewLine}  Searched:{Environment.NewLine}"
            + string.Join(Environment.NewLine, candidates));
    }

    public ILDrawFileSource RequireShadow() =>
        Shadow ?? throw new CommandLineException(
            "No LDCad shadow library found. Run scripts/bootstrap-libraries.ps1, or pass --shadow <path>.");

    private static string FindRepositoryRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (IsRepositoryRoot(directory.FullName))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        return Directory.GetCurrentDirectory();
    }

    /// <summary>Accepts either solution format; the SDK now emits .slnx by default.</summary>
    internal static bool IsRepositoryRoot(string directory) =>
        Directory.Exists(Path.Combine(directory, ".git"))
        || File.Exists(Path.Combine(directory, "TechnicsSimulator.slnx"))
        || File.Exists(Path.Combine(directory, "TechnicsSimulator.sln"));

    public void Dispose()
    {
        Library?.Dispose();
        Shadow?.Dispose();
    }
}

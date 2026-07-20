using TechnicsSim.LDraw.Library;
using TechnicsSim.LDraw.Sources;

namespace TechnicsSim.Tests;

/// <summary>
/// Locates the repository and the optional external libraries. Tests that need real data skip
/// with an explicit reason rather than failing, so a clean checkout without Library/ still runs
/// the full fixture suite.
/// </summary>
public static class TestEnvironment
{
    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    public static string ModelsDirectory { get; } = Path.Combine(RepositoryRoot, "Models");

    private static readonly Lazy<(ILDrawFileSource? Source, LibraryInfo? Info)> LibraryLazy =
        new(() =>
        {
            var located = LibraryLocator.Locate(null, RepositoryRoot);
            return (located?.Source, located?.Info);
        });

    private static readonly Lazy<ShadowLibraryInfo?> ShadowLazy =
        new(() => LibraryLocator.LocateShadow(null, RepositoryRoot));

    public static ILDrawFileSource? Library => LibraryLazy.Value.Source;

    public static LibraryInfo? LibraryInfo => LibraryLazy.Value.Info;

    public static ShadowLibraryInfo? ShadowInfo => ShadowLazy.Value;

    public static ILDrawFileSource? Shadow => ShadowInfo is null
        ? null
        : ShadowSourceLazy.Value;

    private static readonly Lazy<ILDrawFileSource> ShadowSourceLazy =
        new(() => new DirectoryFileSource(ShadowInfo!.Path, "LDCad shadow library"));

    public static bool HasLibrary => Library is not null;

    public static bool HasShadow => ShadowInfo is not null;

    public const string NoLibraryReason =
        "No official LDraw library configured. Run scripts/bootstrap-libraries.ps1 "
        + "or set TECHNICSSIM_LDRAW_PATH.";

    public const string NoShadowReason =
        "No LDCad shadow library checkout. Run scripts/bootstrap-libraries.ps1.";

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            // Accepts either solution format; the SDK now emits .slnx by default.
            if (Directory.Exists(Path.Combine(directory.FullName, ".git"))
                || File.Exists(Path.Combine(directory.FullName, "TechnicsSimulator.slnx"))
                || File.Exists(Path.Combine(directory.FullName, "TechnicsSimulator.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root from the test assembly.");
    }
}

/// <summary>A fact that needs the official LDraw library, skipped with a reason when it is absent.</summary>
public sealed class RealLibraryFactAttribute : FactAttribute
{
    public RealLibraryFactAttribute()
    {
        if (!TestEnvironment.HasLibrary)
        {
            Skip = TestEnvironment.NoLibraryReason;
        }
    }
}

/// <summary>A theory that needs the official LDraw library.</summary>
public sealed class RealLibraryTheoryAttribute : TheoryAttribute
{
    public RealLibraryTheoryAttribute()
    {
        if (!TestEnvironment.HasLibrary)
        {
            Skip = TestEnvironment.NoLibraryReason;
        }
    }
}

/// <summary>A fact that needs both the official library and the shadow checkout.</summary>
public sealed class ShadowFactAttribute : FactAttribute
{
    public ShadowFactAttribute() => Skip = ShadowSkipReason();

    internal static string? ShadowSkipReason() =>
        !TestEnvironment.HasLibrary ? TestEnvironment.NoLibraryReason
        : !TestEnvironment.HasShadow ? TestEnvironment.NoShadowReason
        : null;
}

/// <summary>A theory that needs both the official library and the shadow checkout.</summary>
public sealed class ShadowTheoryAttribute : TheoryAttribute
{
    public ShadowTheoryAttribute() => Skip = ShadowFactAttribute.ShadowSkipReason();
}

using System.Security.Cryptography;
using TechnicsSim.LDraw.Parsing;
using TechnicsSim.LDraw.Sources;

namespace TechnicsSim.LDraw.Library;

/// <summary>How an official-library source was located.</summary>
public enum LibraryKind
{
    Directory,
    ZipArchive,
}

/// <summary>
/// Provenance for the official parts library actually in use. Every CLI report and the
/// About/Diagnostics UI must show this so that a changed library never silently moves a
/// test baseline.
/// </summary>
public sealed record LibraryInfo(
    LibraryKind Kind,
    string Path,
    string DisplayName,
    string ResolvedBy,
    int FileCount,
    string? UpdateTag,
    string? Sha256,
    long SizeBytes);

/// <summary>
/// Provenance for the LDCad shadow library checkout. The commit hash is the whole identity;
/// no checkout timestamp is recorded, because that describes when someone cloned rather than
/// which data they got, and it would churn every golden report.
/// </summary>
public sealed record ShadowLibraryInfo(
    string Path,
    string? GitCommit,
    int PartFileCount,
    int PrimitiveFileCount);

/// <summary>
/// Finds the official LDraw library using the documented configuration order, so that
/// command-line and CI setup is explicit rather than magical.
/// </summary>
public static class LibraryLocator
{
    public const string PathEnvironmentVariable = "TECHNICSSIM_LDRAW_PATH";

    private const string LeoCadFallback = @"C:\Program Files\LeoCAD\library.bin";

    /// <summary>
    /// Locates a library source. Order: explicit path, then the environment variable, then
    /// <c>Library/complete.zip</c> or <c>Library/LDraw/</c>, then the known LeoCAD archive.
    /// </summary>
    /// <param name="explicitPath">A path supplied on the command line, if any.</param>
    /// <param name="repositoryRoot">Repository root used to probe the ignored Library folder.</param>
    public static (ILDrawFileSource Source, LibraryInfo Info)? Locate(
        string? explicitPath,
        string repositoryRoot)
    {
        foreach (var (path, reason) in EnumerateCandidates(explicitPath, repositoryRoot))
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            if (File.Exists(path))
            {
                var source = new ZipFileSource(path);
                return (source, Describe(LibraryKind.ZipArchive, path, reason, source.FileCount, source));
            }

            if (Directory.Exists(path))
            {
                var source = new DirectoryFileSource(path);
                if (source.FileCount == 0)
                {
                    source.Dispose();
                    continue;
                }

                return (source, Describe(LibraryKind.Directory, path, reason, source.FileCount, source));
            }
        }

        return null;
    }

    /// <summary>The candidate paths in configuration order, with why each was considered.</summary>
    public static IEnumerable<(string? Path, string Reason)> EnumerateCandidates(
        string? explicitPath,
        string repositoryRoot)
    {
        yield return (explicitPath, "--ldraw command-line option");
        yield return (Environment.GetEnvironmentVariable(PathEnvironmentVariable),
            $"{PathEnvironmentVariable} environment variable");
        yield return (Path.Combine(repositoryRoot, "Library", "complete.zip"), "Library/complete.zip");
        yield return (Path.Combine(repositoryRoot, "Library", "LDraw"), "Library/LDraw/");
        yield return (LeoCadFallback, "LeoCAD library.bin fallback");
    }

    private static LibraryInfo Describe(
        LibraryKind kind,
        string path,
        string reason,
        int fileCount,
        ILDrawFileSource source)
    {
        var full = Path.GetFullPath(path);
        var size = kind == LibraryKind.ZipArchive ? new FileInfo(full).Length : 0L;

        // Hashing a 130+ MB archive is worth it: it is the only reliable identity for a
        // library snapshot. Directories are not hashed; their contents are not atomic.
        var sha = kind == LibraryKind.ZipArchive ? ComputeSha256(full) : null;

        return new LibraryInfo(kind, full, source.DisplayName, reason, fileCount, ReadUpdateTag(source), sha, size);
    }

    /// <summary>
    /// Reads the library release tag from <c>LDConfig.ldr</c>, whose <c>!LDRAW_ORG</c> header
    /// carries the update stamp, e.g. <c>Configuration UPDATE 2025-08-04</c>.
    /// </summary>
    private static string? ReadUpdateTag(ILDrawFileSource source)
    {
        if (!source.TryRead("ldconfig.ldr", out var file))
        {
            return null;
        }

        var parsed = LDrawParser.Parse(file.Text, "LDConfig.ldr", file.OriginPath);
        foreach (var command in parsed.Root.Commands)
        {
            if (command is Ast.MetaCommand { Keyword: "!LDRAW_ORG" } meta)
            {
                return meta.Arguments;
            }
        }

        return null;
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    /// <summary>Reads provenance for the shadow checkout under <c>Library/LDCadShadowLibrary</c>.</summary>
    public static ShadowLibraryInfo? LocateShadow(string? explicitPath, string repositoryRoot)
    {
        var path = explicitPath ?? Path.Combine(repositoryRoot, "Library", "LDCadShadowLibrary");
        if (!Directory.Exists(path))
        {
            return null;
        }

        var parts = CountFiles(Path.Combine(path, "parts"));
        var primitives = CountFiles(Path.Combine(path, "p"));

        return new ShadowLibraryInfo(Path.GetFullPath(path), ReadGitHead(path), parts, primitives);
    }

    private static int CountFiles(string directory) =>
        Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.dat", SearchOption.AllDirectories).Count()
            : 0;

    /// <summary>
    /// Reads the checked-out commit without shelling out to git, so reports work in
    /// environments where git is unavailable.
    /// </summary>
    private static string? ReadGitHead(string repositoryPath)
    {
        var gitDir = Path.Combine(repositoryPath, ".git");
        if (!Directory.Exists(gitDir))
        {
            return null;
        }

        try
        {
            var head = File.ReadAllText(Path.Combine(gitDir, "HEAD")).Trim();
            if (!head.StartsWith("ref:", StringComparison.Ordinal))
            {
                return head;
            }

            var refName = head[4..].Trim();
            var refPath = Path.Combine(gitDir, refName.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(refPath)
                ? File.ReadAllText(refPath).Trim()
                : ReadPackedRef(gitDir, refName);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string? ReadPackedRef(string gitDir, string refName)
    {
        var packed = Path.Combine(gitDir, "packed-refs");
        if (!File.Exists(packed))
        {
            return null;
        }

        foreach (var line in File.ReadLines(packed))
        {
            if (line.EndsWith(" " + refName, StringComparison.Ordinal))
            {
                return line[..line.IndexOf(' ')];
            }
        }

        return null;
    }
}

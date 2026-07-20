using System.Diagnostics.CodeAnalysis;

namespace TechnicsSim.LDraw.Sources;

/// <summary>
/// Serves files from an extracted LDraw tree. The directory is indexed once by canonical
/// path so lookups behave identically on case-sensitive and case-insensitive file systems.
/// </summary>
public sealed class DirectoryFileSource : ILDrawFileSource
{
    private readonly string _root;
    private readonly Dictionary<string, string> _index;

    public DirectoryFileSource(string rootDirectory, string? displayName = null)
    {
        _root = Path.GetFullPath(rootDirectory);
        DisplayName = displayName ?? _root;
        _index = BuildIndex(_root);
    }

    public string DisplayName { get; }

    /// <summary>The number of indexed files, reported by <c>library-info</c>.</summary>
    public int FileCount => _index.Count;

    public bool Contains(string canonicalPath) => _index.ContainsKey(canonicalPath);

    public bool TryRead(string canonicalPath, [NotNullWhen(true)] out LDrawSourceFile? file)
    {
        if (!_index.TryGetValue(canonicalPath, out var fullPath))
        {
            file = null;
            return false;
        }

        file = new LDrawSourceFile(canonicalPath, fullPath, DisplayName, File.ReadAllText(fullPath));
        return true;
    }

    public IEnumerable<string> EnumeratePaths() => _index.Keys;

    public void Dispose()
    {
    }

    private static Dictionary<string, string> BuildIndex(string root)
    {
        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(root))
        {
            return index;
        }

        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, path);
            var canonical = LDrawName.Canonicalize(relative);

            // A directory tree can legitimately contain both `ldraw/parts/x.dat` and, if the
            // caller pointed at the parent, `parts/x.dat`. First writer wins; the resolver
            // reports ambiguity across sources rather than inside one.
            index.TryAdd(canonical, path);

            if (canonical.StartsWith("ldraw/", StringComparison.Ordinal))
            {
                index.TryAdd(canonical["ldraw/".Length..], path);
            }
        }

        return index;
    }
}

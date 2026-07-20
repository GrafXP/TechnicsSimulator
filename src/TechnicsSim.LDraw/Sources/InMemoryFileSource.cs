using System.Diagnostics.CodeAnalysis;

namespace TechnicsSim.LDraw.Sources;

/// <summary>
/// Serves file text held in memory. Used for MPD-local sections that need to participate
/// in ordinary path resolution, and for the committed parser/resolver test fixtures.
/// </summary>
public sealed class InMemoryFileSource : ILDrawFileSource
{
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);

    public InMemoryFileSource(string displayName) => DisplayName = displayName;

    public string DisplayName { get; }

    public int FileCount => _files.Count;

    /// <summary>Adds or replaces a file. The path is canonicalized on the caller's behalf.</summary>
    public InMemoryFileSource Add(string path, string text)
    {
        _files[LDrawName.Canonicalize(path)] = text;
        return this;
    }

    public bool Contains(string canonicalPath) => _files.ContainsKey(canonicalPath);

    public bool TryRead(string canonicalPath, [NotNullWhen(true)] out LDrawSourceFile? file)
    {
        if (!_files.TryGetValue(canonicalPath, out var text))
        {
            file = null;
            return false;
        }

        file = new LDrawSourceFile(canonicalPath, $"{DisplayName}!{canonicalPath}", DisplayName, text);
        return true;
    }

    public IEnumerable<string> EnumeratePaths() => _files.Keys;

    public void Dispose()
    {
    }
}

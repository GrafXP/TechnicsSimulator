using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;

namespace TechnicsSim.LDraw.Sources;

/// <summary>
/// Serves files straight out of a ZIP archive: an official <c>complete.zip</c> or LeoCAD's
/// <c>library.bin</c>, which is a ZIP holding an <c>ldraw/</c> tree. Reading in place avoids
/// extracting hundreds of megabytes to inspect a small working set.
/// </summary>
public sealed class ZipFileSource : ILDrawFileSource
{
    private readonly ZipArchive _archive;
    private readonly Dictionary<string, ZipArchiveEntry> _index;
    private readonly string _archivePath;
    private readonly object _readLock = new();

    public ZipFileSource(string archivePath, string? displayName = null)
    {
        _archivePath = Path.GetFullPath(archivePath);
        DisplayName = displayName ?? _archivePath;
        _archive = ZipFile.OpenRead(_archivePath);
        _index = BuildIndex(_archive);
    }

    public string DisplayName { get; }

    public int FileCount => _index.Count;

    public bool Contains(string canonicalPath) => _index.ContainsKey(canonicalPath);

    public bool TryRead(string canonicalPath, [NotNullWhen(true)] out LDrawSourceFile? file)
    {
        if (!_index.TryGetValue(canonicalPath, out var entry))
        {
            file = null;
            return false;
        }

        // ZipArchive streams share the underlying file handle and are not thread-safe.
        string text;
        lock (_readLock)
        {
            using var stream = entry.Open();
            using var reader = new StreamReader(stream);
            text = reader.ReadToEnd();
        }

        file = new LDrawSourceFile(canonicalPath, $"{_archivePath}!{entry.FullName}", DisplayName, text);
        return true;
    }

    public IEnumerable<string> EnumeratePaths() => _index.Keys;

    public void Dispose() => _archive.Dispose();

    private static Dictionary<string, ZipArchiveEntry> BuildIndex(ZipArchive archive)
    {
        var index = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);

        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/'))
            {
                continue;
            }

            var canonical = LDrawName.Canonicalize(entry.FullName);
            index.TryAdd(canonical, entry);

            // Both complete.zip and LeoCAD's library.bin wrap the tree in a top-level
            // `ldraw/` folder, so index the unwrapped path too and lookups stay uniform.
            if (canonical.StartsWith("ldraw/", StringComparison.Ordinal))
            {
                index.TryAdd(canonical["ldraw/".Length..], entry);
            }
        }

        return index;
    }
}

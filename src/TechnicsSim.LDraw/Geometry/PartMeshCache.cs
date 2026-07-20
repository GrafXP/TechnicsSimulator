using TechnicsSim.LDraw.Resolution;

namespace TechnicsSim.LDraw.Geometry;

/// <summary>
/// Caches built part geometry by canonical name.
///
/// The cache key includes a source revision so that swapping the official library or reloading
/// a model with changed MPD-local parts cannot serve stale geometry. Without that, a part
/// edited inside an MPD would keep rendering its previous shape for the life of the process.
/// </summary>
public sealed class PartMeshCache
{
    private readonly LDrawResolver _resolver;
    private readonly MeshBuildOptions _options;
    private readonly string _sourceRevision;
    private readonly Dictionary<string, PartMesh> _meshes = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public PartMeshCache(LDrawResolver resolver, string sourceRevision, MeshBuildOptions? options = null)
    {
        _resolver = resolver;
        _sourceRevision = sourceRevision;
        _options = options ?? MeshBuildOptions.Default;
    }

    /// <summary>The revision string this cache's contents were built against.</summary>
    public string SourceRevision => _sourceRevision;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _meshes.Count;
            }
        }
    }

    /// <summary>Cache hits since construction, reported by the performance counters.</summary>
    public int Hits { get; private set; }

    public int Misses { get; private set; }

    /// <summary>
    /// Returns the mesh for a canonical part name, building it on first request. Returns an
    /// empty mesh rather than throwing when the part cannot be resolved, so one bad reference
    /// does not abort loading an entire model.
    /// </summary>
    public PartMesh Get(string canonicalPartName)
    {
        lock (_gate)
        {
            if (_meshes.TryGetValue(canonicalPartName, out var cached))
            {
                Hits++;
                return cached;
            }

            Misses++;

            var resolved = _resolver.Resolve(canonicalPartName);
            var mesh = resolved.IsResolved
                ? new PartMeshBuilder(_resolver, _options).Build(resolved.Document!)
                : new PartMesh(canonicalPartName, [], [], Bounds.Empty, 0, 0, [canonicalPartName]);

            _meshes[canonicalPartName] = mesh;
            return mesh;
        }
    }

    /// <summary>
    /// Builds the static geometry written directly into a model's sections. Not cached: it is
    /// specific to one model root rather than reusable across instances.
    /// </summary>
    public PartMesh BuildInlineGeometry(Ast.LDrawDocument root) =>
        new PartMeshBuilder(_resolver, _options).BuildModelInlineGeometry(root);
}

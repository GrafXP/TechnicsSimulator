using System.Collections.Immutable;
using TechnicsSim.LDraw.Ast;
using TechnicsSim.LDraw.Expansion;
using TechnicsSim.LDraw.Resolution;
using TechnicsSim.LDraw.Sources;

namespace TechnicsSim.LDraw.Shadow;

/// <summary>How a part obtains snap features, if at all.</summary>
public enum ShadowCoverage
{
    /// <summary>No shadow data anywhere in the part's geometry tree.</summary>
    None,

    /// <summary>Features come only from primitives and subparts below the part.</summary>
    Inherited,

    /// <summary>The part has its own shadow file with features.</summary>
    Direct,

    /// <summary>The part has its own shadow file and also inherits from below.</summary>
    DirectAndInherited,
}

/// <summary>Shadow coverage for one canonical part.</summary>
/// <param name="DirectFeatureCount">Snap features declared on the part's own shadow file.</param>
/// <param name="InheritedFeatureCount">
/// Snap features found below the part. This is a reachability count, not the mechanics
/// effective-feature count: it does not apply transforms, grids, scale/mirror policies,
/// or <c>SNAP_CLEAR</c>, all of which change the final total.
/// </param>
/// <param name="ClearCount">
/// <c>SNAP_CLEAR</c> metas seen. A non-zero count is a warning that the naive inherited total
/// above overstates the effective one.
/// </param>
/// <param name="MirroredChildReferences">
/// References inside the part tree whose transform has a negative determinant. The effective
/// extractor decides, per feature, whether the snap <c>mirror</c> policy accepts or rejects inheritance
/// across each of these. Counting them now sizes that work instead of guessing at it.
/// </param>
/// <param name="ScaledChildReferences">
/// References whose transform is not a rigid motion (non-unit axis lengths). Same reasoning
/// as <paramref name="MirroredChildReferences"/>, for the snap <c>scale</c> policy.
/// </param>
public sealed record PartShadowCoverage(
    string CanonicalPartName,
    ShadowCoverage Coverage,
    int DirectFeatureCount,
    int InheritedFeatureCount,
    int ClearCount,
    ImmutableDictionary<string, int> FeatureTypeCounts,
    ImmutableArray<string> ContributingShadowFiles,
    bool HasUnresolvedGeometry,
    int MirroredChildReferences,
    int ScaledChildReferences);

/// <summary>
/// Answers "does this part have snap features, directly or by inheritance?" for the Phase 0
/// coverage report.
///
/// This is deliberately a reachability probe rather than the real extractor. It walks the
/// official geometry tree and asks whether a matching shadow file exists at each node. It does
/// not compose transforms, expand grids, honour scale/mirror inheritance policies, resolve
/// <c>SNAP_INCL</c>, or apply <c>SNAP_CLEAR</c> ordering. Those belong to the mechanics extractor;
/// conflating the two would produce a coverage number that quietly disagrees with it.
/// </summary>
public sealed class ShadowCoverageProbe
{
    private readonly LDrawResolver _resolver;
    private readonly ILDrawFileSource _shadowSource;
    private readonly Dictionary<string, PartShadowCoverage> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ImmutableArray<ShadowMeta>> _shadowCache = new(StringComparer.Ordinal);

    public ShadowCoverageProbe(LDrawResolver resolver, ILDrawFileSource shadowSource)
    {
        _resolver = resolver;
        _shadowSource = shadowSource;
    }

    /// <summary>Computes (and caches) coverage for one canonical part name.</summary>
    public PartShadowCoverage Probe(string canonicalPartName)
    {
        if (_cache.TryGetValue(canonicalPartName, out var cached))
        {
            return cached;
        }

        // Seed the cache before descending so a cyclic geometry tree cannot recurse forever.
        _cache[canonicalPartName] = Empty(canonicalPartName);

        var result = ProbeUncached(canonicalPartName);
        _cache[canonicalPartName] = result;
        return result;
    }

    private static PartShadowCoverage Empty(string canonicalPartName) => new(
        canonicalPartName, ShadowCoverage.None, 0, 0, 0,
        ImmutableDictionary<string, int>.Empty, [], false, 0, 0);

    /// <summary>Mutable running totals for one part's traversal.</summary>
    private sealed class Accumulator
    {
        public readonly Dictionary<string, int> TypeCounts = new(StringComparer.Ordinal);
        public readonly ImmutableArray<string>.Builder Files = ImmutableArray.CreateBuilder<string>();
        public readonly HashSet<string> Visited = new(StringComparer.Ordinal);
        public int Direct;
        public int Inherited;
        public int Clears;
        public int Mirrored;
        public int Scaled;
        public bool Unresolved;
    }

    private PartShadowCoverage ProbeUncached(string canonicalPartName)
    {
        var acc = new Accumulator();
        acc.Visited.Add(canonicalPartName);
        acc.Direct = CountFeatures(canonicalPartName, acc);

        var resolved = _resolver.Resolve(canonicalPartName);
        if (resolved.IsResolved)
        {
            CountInherited(resolved.Document!, acc);
        }
        else
        {
            acc.Unresolved = true;
        }

        var coverage = (acc.Direct > 0, acc.Inherited > 0) switch
        {
            (true, true) => ShadowCoverage.DirectAndInherited,
            (true, false) => ShadowCoverage.Direct,
            (false, true) => ShadowCoverage.Inherited,
            _ => ShadowCoverage.None,
        };

        return new PartShadowCoverage(
            canonicalPartName,
            coverage,
            acc.Direct,
            acc.Inherited,
            acc.Clears,
            acc.TypeCounts.ToImmutableDictionary(StringComparer.Ordinal),
            acc.Files.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray(),
            acc.Unresolved,
            acc.Mirrored,
            acc.Scaled);
    }

    private void CountInherited(LDrawDocument document, Accumulator acc)
    {
        foreach (var reference in document.References)
        {
            // Inspect every reference site, even one whose target was already visited: the
            // Effective mirror and scale policies are evaluated per reference, not per part.
            ClassifyTransform(reference.Transform, acc);

            var resolved = _resolver.Resolve(reference.TargetName);
            if (!resolved.IsResolved)
            {
                acc.Unresolved = true;
                continue;
            }

            var child = resolved.Document!;
            if (!acc.Visited.Add(child.CanonicalName))
            {
                continue;
            }

            acc.Inherited += CountFeatures(child.CanonicalName, acc);
            CountInherited(child, acc);
        }
    }

    private static void ClassifyTransform(System.Numerics.Matrix4x4 transform, Accumulator acc)
    {
        var x = new System.Numerics.Vector3(transform.M11, transform.M12, transform.M13);
        var y = new System.Numerics.Vector3(transform.M21, transform.M22, transform.M23);
        var z = new System.Numerics.Vector3(transform.M31, transform.M32, transform.M33);

        // A negative determinant means the child is reflected, which flips feature handedness.
        if (System.Numerics.Vector3.Dot(System.Numerics.Vector3.Cross(x, y), z) < 0f)
        {
            acc.Mirrored++;
        }

        const float tolerance = 1e-3f;
        if (Math.Abs(x.Length() - 1f) > tolerance
            || Math.Abs(y.Length() - 1f) > tolerance
            || Math.Abs(z.Length() - 1f) > tolerance)
        {
            acc.Scaled++;
        }
    }

    /// <summary>Counts snap features on the shadow file matching one canonical name.</summary>
    private int CountFeatures(string canonicalName, Accumulator acc)
    {
        var metas = LoadShadow(canonicalName);
        if (metas.IsEmpty)
        {
            return 0;
        }

        var features = 0;
        var fileClears = 0;

        foreach (var meta in metas)
        {
            if (meta.Name == "SNAP_CLEAR")
            {
                fileClears++;
                continue;
            }

            if (!meta.IsSnapFeature && meta.Name != "SNAP_INCL")
            {
                continue;
            }

            acc.TypeCounts[meta.Name] = acc.TypeCounts.GetValueOrDefault(meta.Name) + 1;
            features++;
        }

        acc.Clears += fileClears;

        if (features > 0 || fileClears > 0)
        {
            acc.Files.Add(metas[0].SourceFile);
        }

        return features;
    }

    /// <summary>
    /// Loads the shadow patch for a canonical name. The shadow tree mirrors the official one,
    /// so <c>3705.dat</c> is looked up as <c>parts/3705.dat</c> and <c>axlehole.dat</c> as
    /// <c>p/axlehole.dat</c>.
    /// </summary>
    private ImmutableArray<ShadowMeta> LoadShadow(string canonicalName)
    {
        if (_shadowCache.TryGetValue(canonicalName, out var cached))
        {
            return cached;
        }

        var result = ImmutableArray<ShadowMeta>.Empty;

        foreach (var candidate in new[] { $"parts/{canonicalName}", $"p/{canonicalName}", canonicalName })
        {
            if (!_shadowSource.TryRead(candidate, out var file))
            {
                continue;
            }

            var parsed = Parsing.LDrawParser.Parse(file.Text, canonicalName, file.OriginPath);
            result = ShadowMetaParser.Extract(parsed.Root, candidate);
            break;
        }

        _shadowCache[canonicalName] = result;
        return result;
    }
}

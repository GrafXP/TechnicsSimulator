using System.Collections.Immutable;
using TechnicsSim.LDraw.Ast;
using TechnicsSim.LDraw.Parsing;
using TechnicsSim.LDraw.Sources;

namespace TechnicsSim.LDraw.Resolution;

/// <summary>Where a resolved document was found. Drives part-versus-geometry classification.</summary>
public enum ResolutionOrigin
{
    /// <summary>A <c>0 FILE</c> section inside the MPD being loaded. Always wins.</summary>
    MpdLocal,

    /// <summary>A loose file next to the model on disk.</summary>
    ModelDirectory,

    /// <summary>The official library's <c>parts/</c> tree, excluding <c>parts/s/</c>.</summary>
    LibraryPart,

    /// <summary>The official library's <c>parts/s/</c> subpart tree.</summary>
    LibrarySubpart,

    /// <summary>The official library's <c>p/</c> primitive tree.</summary>
    LibraryPrimitive,

    /// <summary>The official library's <c>models/</c> tree.</summary>
    LibraryModel,
}

/// <summary>Why a reference could not be resolved.</summary>
public enum ResolutionFailure
{
    None,

    /// <summary>No configured source had the file under any candidate path.</summary>
    Missing,

    /// <summary>The reference chain re-entered a file already being expanded.</summary>
    Cyclic,
}

/// <summary>The outcome of resolving one type-1 target name.</summary>
public sealed record ResolvedReference(
    string RequestedName,
    LDrawDocument? Document,
    ResolutionOrigin Origin,
    ResolutionFailure Failure,
    string? OriginPath,
    string? SourceName,
    ImmutableArray<string> AmbiguousAlternatives)
{
    public bool IsResolved => Document is not null && Failure == ResolutionFailure.None;
}

/// <summary>
/// Resolves type-1 target names to parsed documents.
///
/// The ordering is the single most important rule here. MPD-local sections must win before
/// any external library file: <c>8275-1.mpd</c> embeds <c>8275 - LS70.dat</c> as an
/// <c>Unofficial_Part</c>, and a <c>.dat</c> suffix is no evidence that a reference points at
/// the official library.
/// </summary>
public sealed class LDrawResolver
{
    private readonly Dictionary<string, LDrawDocument> _mpdLocal;
    private readonly IReadOnlyList<ILDrawFileSource> _modelDirectorySources;
    private readonly IReadOnlyList<ILDrawFileSource> _librarySources;
    private readonly Dictionary<string, ResolvedReference> _cache = new(StringComparer.Ordinal);
    private readonly List<LDrawParseIssue> _parseIssues = [];

    public LDrawResolver(
        IEnumerable<LDrawDocument> mpdLocalDocuments,
        IEnumerable<ILDrawFileSource> librarySources,
        IEnumerable<ILDrawFileSource>? modelDirectorySources = null)
    {
        _mpdLocal = new Dictionary<string, LDrawDocument>(StringComparer.Ordinal);
        foreach (var document in mpdLocalDocuments)
        {
            // The first definition of a duplicated section name wins, matching how
            // LDraw editors treat a repeated `0 FILE`.
            _mpdLocal.TryAdd(document.CanonicalName, document);
        }

        _librarySources = librarySources.ToArray();
        _modelDirectorySources = modelDirectorySources?.ToArray() ?? [];
    }

    /// <summary>Parse issues collected from every document this resolver has loaded.</summary>
    public IReadOnlyList<LDrawParseIssue> ParseIssues => _parseIssues;

    /// <summary>The MPD-local sections, keyed by canonical name.</summary>
    public IReadOnlyDictionary<string, LDrawDocument> MpdLocalDocuments => _mpdLocal;

    /// <summary>
    /// Resolves a reference by name. Results are cached per canonical name because a model
    /// like 8275 references the same part thousands of times.
    /// </summary>
    public ResolvedReference Resolve(string targetName)
    {
        var canonical = LDrawName.Canonicalize(targetName);
        if (_cache.TryGetValue(canonical, out var cached))
        {
            return cached with { RequestedName = targetName };
        }

        var resolved = ResolveUncached(targetName, canonical);
        _cache[canonical] = resolved;
        return resolved;
    }

    private ResolvedReference ResolveUncached(string requestedName, string canonical)
    {
        // 1. MPD-local sections, before anything external.
        if (_mpdLocal.TryGetValue(canonical, out var local))
        {
            return new ResolvedReference(
                requestedName, local, ResolutionOrigin.MpdLocal, ResolutionFailure.None,
                local.OriginPath, "MPD", []);
        }

        // 2. Files sitting next to the model on disk.
        foreach (var source in _modelDirectorySources)
        {
            if (TryLoad(source, canonical, requestedName, ResolutionOrigin.ModelDirectory, out var found))
            {
                return found;
            }
        }

        // 3. Configured official-library sources, in candidate-path order.
        var alternatives = ImmutableArray.CreateBuilder<string>();
        ResolvedReference? first = null;

        foreach (var (candidatePath, origin) in EnumerateCandidatePaths(canonical))
        {
            foreach (var source in _librarySources)
            {
                if (!source.Contains(candidatePath))
                {
                    continue;
                }

                if (first is null)
                {
                    if (TryLoad(source, candidatePath, requestedName, origin, out var found))
                    {
                        first = found;
                    }
                }
                else
                {
                    // A second hit under a different library root is worth surfacing:
                    // it usually means an unofficial file is shadowing an official one.
                    alternatives.Add($"{source.DisplayName}!{candidatePath}");
                }
            }
        }

        if (first is not null)
        {
            return first with { AmbiguousAlternatives = alternatives.ToImmutable() };
        }

        return new ResolvedReference(
            requestedName, null, ResolutionOrigin.LibraryPart, ResolutionFailure.Missing, null, null, []);
    }

    private bool TryLoad(
        ILDrawFileSource source,
        string candidatePath,
        string requestedName,
        ResolutionOrigin origin,
        out ResolvedReference resolved)
    {
        if (!source.TryRead(candidatePath, out var file))
        {
            resolved = null!;
            return false;
        }

        // A library file is a single document; an embedded `0 FILE` would be unusual but is
        // handled by taking the root section.
        var parsed = LDrawParser.Parse(file.Text, requestedName, file.OriginPath);
        _parseIssues.AddRange(parsed.Issues);

        resolved = new ResolvedReference(
            requestedName, parsed.Root, origin, ResolutionFailure.None,
            file.OriginPath, file.SourceName, []);
        return true;
    }

    /// <summary>
    /// The library-relative paths to try for a reference, most specific first.
    ///
    /// A prefixed reference such as <c>s/4-4disc.dat</c> or <c>48/1-4cyli.dat</c> is covered by
    /// prepending the library root, so no special casing is needed beyond classifying the
    /// origin of whichever candidate matched.
    /// </summary>
    private static IEnumerable<(string Path, ResolutionOrigin Origin)> EnumerateCandidatePaths(string canonical)
    {
        yield return ($"parts/{canonical}",
            canonical.StartsWith("s/", StringComparison.Ordinal)
                ? ResolutionOrigin.LibrarySubpart
                : ResolutionOrigin.LibraryPart);

        yield return ($"p/{canonical}", ResolutionOrigin.LibraryPrimitive);
        yield return ($"models/{canonical}", ResolutionOrigin.LibraryModel);

        // An already-rooted path, e.g. a reference written as `parts/3705.dat`.
        yield return (canonical, ClassifyRootedPath(canonical));
    }

    private static ResolutionOrigin ClassifyRootedPath(string canonical) => canonical switch
    {
        _ when canonical.StartsWith("parts/s/", StringComparison.Ordinal) => ResolutionOrigin.LibrarySubpart,
        _ when canonical.StartsWith("parts/", StringComparison.Ordinal) => ResolutionOrigin.LibraryPart,
        _ when canonical.StartsWith("p/", StringComparison.Ordinal) => ResolutionOrigin.LibraryPrimitive,
        _ when canonical.StartsWith("models/", StringComparison.Ordinal) => ResolutionOrigin.LibraryModel,
        _ => ResolutionOrigin.LibraryPart,
    };
}

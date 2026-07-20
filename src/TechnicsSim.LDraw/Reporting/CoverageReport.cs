namespace TechnicsSim.LDraw.Reporting;

/// <summary>
/// The <c>coverage</c> command's JSON schema. Golden report tests diff these, so field names
/// and shapes are part of the contract; bump <see cref="CoverageReport.SchemaVersion"/> when
/// they change.
/// </summary>
public sealed record CoverageReport(
    int SchemaVersion,
    ModelReport Model,
    SourcesReport Sources,
    CountsReport Counts,
    ResolutionReport Resolution,
    ShadowReport Shadow)
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Drops the <see cref="Sources"/> block, which holds machine-specific absolute paths and
    /// the library hash. Golden report comparisons use this so a diff shows a real change in
    /// model interpretation rather than a different checkout location. The committed golden
    /// file still records provenance for the human reading it.
    /// </summary>
    public CoverageReport WithoutSourceProvenance() =>
        this with { Sources = new SourcesReport(null, null) };

    /// <summary>
    /// Rewrites absolute paths under <paramref name="repositoryRoot"/> to a <c>&lt;repo&gt;</c>
    /// placeholder. Committed golden files use this so they do not record whoever generated
    /// them, and so regenerating on another machine produces no spurious diff.
    /// </summary>
    public CoverageReport WithNormalizedPaths(string repositoryRoot)
    {
        var root = repositoryRoot.TrimEnd('/', '\\');

        string Normalize(string path) =>
            path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? "<repo>" + path[root.Length..].Replace('\\', '/')
                : path;

        return this with
        {
            Sources = new SourcesReport(
                Sources.OfficialLibrary is { } library
                    ? library with { Path = Normalize(library.Path) }
                    : null,
                Sources.ShadowLibrary is { } shadow
                    ? shadow with { Path = Normalize(shadow.Path) }
                    : null),
        };
    }
}

public sealed record ModelReport(string File, string RootSection, int MpdSections);

public sealed record SourcesReport(OfficialLibraryReport? OfficialLibrary, ShadowSourceReport? ShadowLibrary);

public sealed record OfficialLibraryReport(
    string Kind,
    string Path,
    string ResolvedBy,
    string? UpdateTag,
    string? Sha256,
    long SizeBytes,
    int FileCount);

public sealed record ShadowSourceReport(
    string Path,
    string? Commit,
    int PartFiles,
    int PrimitiveFiles);

/// <summary>
/// The three counts that must never be conflated: physical type-1 lines in the MPD, expanded
/// logical part instances, and distinct logical parts.
/// </summary>
public sealed record CountsReport(
    int PhysicalSubfileLines,
    int LogicalPartInstances,
    int DistinctLogicalParts,
    int SubmodelReferences,
    IReadOnlyDictionary<string, int> PhysicalInlineGeometryLines,
    IReadOnlyDictionary<string, int> ExpandedInlineGeometryLines);

public sealed record ResolutionReport(
    int UnresolvedCount,
    int AmbiguousCount,
    int ParseIssueCount,
    IReadOnlyList<UnresolvedEntry> Unresolved,
    IReadOnlyList<string> Ambiguous,
    IReadOnlyList<string> ParseIssues);

public sealed record UnresolvedEntry(
    string Name,
    string Failure,
    string DeclaringDocument,
    int Line,
    string ReferenceChain);

/// <summary>
/// Shadow coverage counted two ways. Unique-part coverage says how much catalogue work
/// remains; instance-weighted coverage says how much of the assembled model it affects.
/// A part used 1,630 times and a part used once are not equally urgent.
/// </summary>
public sealed record ShadowReport(
    CoverageBreakdown ByUniquePart,
    CoverageBreakdown ByInstance,
    IReadOnlyDictionary<string, int> FeatureTypeHistogram,
    int TotalDirectFeatures,
    int TotalInheritedFeatures,
    int SnapClearCount,
    int MirroredChildReferences,
    int ScaledChildReferences,
    IReadOnlyList<UncoveredPart> HighUseUncoveredParts,
    IReadOnlyList<PartCoverageEntry> Parts);

public sealed record CoverageBreakdown(int Direct, int Inherited, int DirectAndInherited, int None);

public sealed record UncoveredPart(string Part, int Instances);

public sealed record PartCoverageEntry(
    string Part,
    int Instances,
    string Coverage,
    int DirectFeatures,
    int InheritedFeatures,
    int SnapClears,
    bool HasUnresolvedGeometry);

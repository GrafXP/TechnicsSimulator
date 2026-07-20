using System.Text.Json;
using System.Text.Json.Serialization;
using TechnicsSim.LDraw.Library;
using TechnicsSim.LDraw.Shadow;
using TechnicsSim.LDraw.Sources;

namespace TechnicsSim.LDraw.Reporting;

/// <summary>
/// Builds the coverage report from a loaded model. It lives in the core library rather than
/// the CLI so the command line, the golden tests, and the eventual diagnostics UI all emit
/// byte-identical results instead of three drifting implementations.
/// </summary>
public static class CoverageReportBuilder
{
    /// <summary>Serialization settings shared by the CLI and the golden comparison.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static CoverageReport Build(
        LoadedModel model,
        ILDrawFileSource shadowSource,
        LibraryInfo? libraryInfo,
        ShadowLibraryInfo? shadowInfo)
    {
        var expansion = model.Expansion;
        var probe = new ShadowCoverageProbe(model.Resolver, shadowSource);

        var byPart = new Dictionary<ShadowCoverage, int>();
        var byInstance = new Dictionary<ShadowCoverage, int>();
        var histogram = new Dictionary<string, int>(StringComparer.Ordinal);
        var entries = new List<PartCoverageEntry>();
        var uncovered = new List<UncoveredPart>();

        var totalDirect = 0;
        var totalInherited = 0;
        var totalClears = 0;
        var totalMirrored = 0;
        var totalScaled = 0;

        // Ordinal ordering keeps the JSON stable across machines and cultures.
        foreach (var (part, instances) in expansion.PartUsage.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var coverage = probe.Probe(part);

            byPart[coverage.Coverage] = byPart.GetValueOrDefault(coverage.Coverage) + 1;
            byInstance[coverage.Coverage] = byInstance.GetValueOrDefault(coverage.Coverage) + instances;

            foreach (var (type, count) in coverage.FeatureTypeCounts)
            {
                histogram[type] = histogram.GetValueOrDefault(type) + count;
            }

            totalDirect += coverage.DirectFeatureCount;
            totalInherited += coverage.InheritedFeatureCount;
            totalClears += coverage.ClearCount;
            totalMirrored += coverage.MirroredChildReferences;
            totalScaled += coverage.ScaledChildReferences;

            entries.Add(new PartCoverageEntry(
                part,
                instances,
                coverage.Coverage.ToString(),
                coverage.DirectFeatureCount,
                coverage.InheritedFeatureCount,
                coverage.ClearCount,
                coverage.HasUnresolvedGeometry));

            if (coverage.Coverage == ShadowCoverage.None)
            {
                uncovered.Add(new UncoveredPart(part, instances));
            }
        }

        return new CoverageReport(
            CoverageReport.CurrentSchemaVersion,
            new ModelReport(Path.GetFileName(model.Path), model.Root.Name, model.Sections.Length),
            new SourcesReport(
                libraryInfo is { } li
                    ? new OfficialLibraryReport(
                        li.Kind.ToString(), li.Path, li.ResolvedBy, li.UpdateTag,
                        li.Sha256, li.SizeBytes, li.FileCount)
                    : null,
                shadowInfo is { } si
                    ? new ShadowSourceReport(
                        si.Path, si.GitCommit, si.PartFileCount, si.PrimitiveFileCount)
                    : null),
            new CountsReport(
                model.PhysicalSubfileLineCount,
                expansion.Instances.Length,
                expansion.PartUsage.Count,
                expansion.SubmodelReferenceCount,
                ByLineType(model.PhysicalGeometryLineCounts),
                ByLineType(expansion.ExpandedInlineGeometryLines)),
            new ResolutionReport(
                expansion.Unresolved.Length,
                expansion.AmbiguousReferences.Length,
                model.ParseIssues.Length,
                expansion.Unresolved
                    .Select(u => new UnresolvedEntry(
                        u.RequestedName, u.Failure.ToString(), u.DeclaringDocument,
                        u.LineNumber, u.ReferenceChain))
                    .ToList(),
                expansion.AmbiguousReferences.ToList(),
                model.ParseIssues
                    .Select(i => $"{i.DocumentName}:{i.LineNumber} {i.Reason}")
                    .ToList()),
            new ShadowReport(
                Breakdown(byPart),
                Breakdown(byInstance),
                histogram.OrderBy(h => h.Key, StringComparer.Ordinal)
                    .ToDictionary(h => h.Key, h => h.Value, StringComparer.Ordinal),
                totalDirect,
                totalInherited,
                totalClears,
                totalMirrored,
                totalScaled,
                uncovered
                    .OrderByDescending(u => u.Instances)
                    .ThenBy(u => u.Part, StringComparer.Ordinal)
                    .Take(30)
                    .ToList(),
                entries));
    }

    public static string ToJson(CoverageReport report) =>
        JsonSerializer.Serialize(report, JsonOptions);

    /// <summary>Renders a line-type keyed count as JSON-friendly names such as "type5".</summary>
    private static Dictionary<string, int> ByLineType(IReadOnlyDictionary<int, int> counts) =>
        counts.OrderBy(c => c.Key).ToDictionary(c => $"type{c.Key}", c => c.Value, StringComparer.Ordinal);

    private static CoverageBreakdown Breakdown(IReadOnlyDictionary<ShadowCoverage, int> counts) => new(
        counts.GetValueOrDefault(ShadowCoverage.Direct),
        counts.GetValueOrDefault(ShadowCoverage.Inherited),
        counts.GetValueOrDefault(ShadowCoverage.DirectAndInherited),
        counts.GetValueOrDefault(ShadowCoverage.None));
}

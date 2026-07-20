using System.Collections.Immutable;
using TechnicsSim.LDraw.Ast;
using TechnicsSim.LDraw.Expansion;
using TechnicsSim.LDraw.Parsing;
using TechnicsSim.LDraw.Resolution;
using TechnicsSim.LDraw.Sources;

namespace TechnicsSim.LDraw;

/// <summary>A model file after parsing, resolution, and logical expansion.</summary>
public sealed record LoadedModel(
    string Path,
    ImmutableArray<LDrawDocument> Sections,
    LDrawResolver Resolver,
    ModelExpansion Expansion,
    ImmutableArray<LDrawParseIssue> ParseIssues)
{
    public LDrawDocument Root => Expansion.Root;

    /// <summary>Physical type-1 lines across all MPD sections, before any expansion.</summary>
    public int PhysicalSubfileLineCount => Sections.Sum(s => s.References.Count());

    /// <summary>
    /// Physical geometry lines written into MPD sections, keyed by LDraw line type and counted
    /// once each. Compare with <see cref="ModelExpansion.ExpandedInlineGeometryLines"/>, which
    /// counts the same lines once per traversal.
    /// </summary>
    public ImmutableDictionary<int, int> PhysicalGeometryLineCounts { get; } = Sections
        .SelectMany(s => s.Commands)
        .Where(c => c.LineType is 2 or 3 or 4 or 5)
        .GroupBy(c => c.LineType)
        .ToImmutableDictionary(g => g.Key, g => g.Count());
}

/// <summary>Loads an LDraw model file end to end: parse, resolve, expand.</summary>
public static class ModelLoader
{
    public static LoadedModel Load(
        string modelPath,
        IEnumerable<ILDrawFileSource> librarySources,
        string? rootSectionName = null)
    {
        var fullPath = System.IO.Path.GetFullPath(modelPath);
        var text = File.ReadAllText(fullPath);
        var defaultName = System.IO.Path.GetFileName(fullPath);

        var parsed = LDrawParser.Parse(text, defaultName, fullPath);

        // Loose .ldr files next to the model are a legitimate reference target, but they
        // must never outrank the MPD's own sections.
        var modelDirectory = System.IO.Path.GetDirectoryName(fullPath);
        var directorySources = modelDirectory is null
            ? Array.Empty<ILDrawFileSource>()
            : [new DirectoryFileSource(modelDirectory, $"model directory ({modelDirectory})")];

        var resolver = new LDrawResolver(parsed.Documents, librarySources, directorySources);

        var root = rootSectionName is null
            ? parsed.Root
            : parsed.Documents.FirstOrDefault(
                  d => d.CanonicalName == LDrawName.Canonicalize(rootSectionName))
              ?? throw new ArgumentException(
                  $"No section named '{rootSectionName}' in {defaultName}.", nameof(rootSectionName));

        var expansion = new LogicalPartExpander(resolver).Expand(root);

        var issues = parsed.Issues.AddRange(resolver.ParseIssues);
        return new LoadedModel(fullPath, parsed.Documents, resolver, expansion, issues);
    }
}

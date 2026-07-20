using System.Collections.Immutable;
using System.Numerics;
using TechnicsSim.LDraw.Ast;
using TechnicsSim.LDraw.Resolution;

namespace TechnicsSim.LDraw.Expansion;

/// <summary>
/// What a referenced document is, for the purpose of deciding whether expansion stops.
/// Determined from <c>!LDRAW_ORG</c> first and provider origin second, never from the
/// file extension alone.
/// </summary>
public enum LogicalKind
{
    /// <summary>A model or submodel. Expansion descends through it.</summary>
    Model,

    /// <summary>A part or shortcut. This is a logical part boundary; expansion stops.</summary>
    Part,

    /// <summary>A part-internal subpart (<c>parts/s/</c>). Geometry, not an independent part.</summary>
    Subpart,

    /// <summary>A primitive (<c>p/</c>, <c>p/48/</c>, <c>p/8/</c>). Geometry.</summary>
    Primitive,
}

/// <summary>Classifies resolved documents into <see cref="LogicalKind"/>.</summary>
public static class LogicalClassifier
{
    public static LogicalKind Classify(LDrawDocument document, ResolutionOrigin origin)
    {
        // The declared header is authoritative when present. This is what makes the MPD-local
        // `8275 - LS70.dat` an Unofficial_Part rather than a submodel.
        switch (document.OrgKind)
        {
            case LDrawOrgKind.Part:
            case LDrawOrgKind.Shortcut:
                return LogicalKind.Part;
            case LDrawOrgKind.Subpart:
                return LogicalKind.Subpart;
            case LDrawOrgKind.Primitive:
            case LDrawOrgKind.HiResPrimitive:
            case LDrawOrgKind.LowResPrimitive:
                return LogicalKind.Primitive;
            case LDrawOrgKind.Model:
                return LogicalKind.Model;
        }

        // No usable header: fall back to where the file came from.
        return origin switch
        {
            ResolutionOrigin.LibraryPart => LogicalKind.Part,
            ResolutionOrigin.LibrarySubpart => LogicalKind.Subpart,
            ResolutionOrigin.LibraryPrimitive => LogicalKind.Primitive,
            _ => LogicalKind.Model,
        };
    }
}

/// <summary>
/// One expanded logical LEGO part in the assembled model: the unit that carries a mesh,
/// snap features, and eventually a place in the mechanism graph.
/// </summary>
/// <param name="InstanceId">
/// A stable hierarchical identifier built from the defining section and the source line of
/// each reference along the path, e.g.
/// <c>8275 - main.ldr@16|8275 - m-1.ldr@27</c>. Sidecar overrides refer to these.
/// </param>
/// <param name="PartName">The reference name exactly as written in the model.</param>
/// <param name="CanonicalPartName">The canonical name used for catalog and shadow lookups.</param>
/// <param name="Transform">The accumulated model-space transform, in LDU.</param>
/// <param name="Colour">The effective colour after resolving inherited colour 16.</param>
/// <param name="Depth">Nesting depth below the root section.</param>
public sealed record LogicalPartInstance(
    string InstanceId,
    string PartName,
    string CanonicalPartName,
    Matrix4x4 Transform,
    int Colour,
    int Depth,
    ResolutionOrigin Origin,
    string? OriginPath);

/// <summary>A reference that could not be resolved, with the chain that reached it.</summary>
/// <param name="ReferenceChain">Human-readable path from the root section to the bad reference.</param>
public sealed record UnresolvedReference(
    string RequestedName,
    ResolutionFailure Failure,
    string ReferenceChain,
    string DeclaringDocument,
    int LineNumber);

/// <summary>
/// A reference made from a model context that resolved to a subpart or primitive rather than
/// a part. These are the classification judgement calls, so they are listed rather than
/// silently folded into a geometry counter.
/// </summary>
public sealed record NonPartReference(
    string RequestedName,
    string CanonicalName,
    LogicalKind Kind,
    ResolutionOrigin Origin,
    string DeclaringDocument,
    int LineNumber);

/// <summary>The result of expanding a model into logical part instances.</summary>
/// <param name="ExpandedInlineGeometryLines">
/// Geometry lines written directly into model sections, counted once per traversal. A submodel
/// referenced twice contributes its lines twice, so this exceeds the physical line count in the
/// same way that logical instances exceed physical type-1 lines. Keyed by LDraw line type.
/// </param>
public sealed record ModelExpansion(
    LDrawDocument Root,
    ImmutableArray<LogicalPartInstance> Instances,
    ImmutableArray<UnresolvedReference> Unresolved,
    ImmutableArray<string> AmbiguousReferences,
    ImmutableArray<NonPartReference> NonPartReferences,
    int SubmodelReferenceCount,
    ImmutableDictionary<int, int> ExpandedInlineGeometryLines)
{
    /// <summary>Distinct canonical part names, with how many instances each contributed.</summary>
    public IReadOnlyDictionary<string, int> PartUsage { get; } =
        Instances
            .GroupBy(i => i.CanonicalPartName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

    /// <summary>Total expanded inline geometry lines across all line types.</summary>
    public int TotalExpandedInlineGeometryLines => ExpandedInlineGeometryLines.Values.Sum();

    /// <summary>
    /// Expanded camera-dependent type-5 edges. Phase 2 of the renderer defers these, so their
    /// count is the size of what is being deferred.
    /// </summary>
    public int ExpandedConditionalLines => ExpandedInlineGeometryLines.GetValueOrDefault(5);
}

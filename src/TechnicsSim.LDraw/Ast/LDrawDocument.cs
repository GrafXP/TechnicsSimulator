using System.Collections.Immutable;

namespace TechnicsSim.LDraw.Ast;

/// <summary>
/// The value of the <c>!LDRAW_ORG</c> header, which is the authoritative statement of
/// what a file is. Extension alone is not sufficient: <c>8275 - LS70.dat</c> is an
/// MPD-local <see cref="UnofficialPart"/>, and plenty of models ship as <c>.dat</c>.
/// </summary>
public enum LDrawOrgKind
{
    /// <summary>No <c>!LDRAW_ORG</c> header was present; fall back to provider origin.</summary>
    Unknown,
    Model,
    Part,
    Shortcut,
    Subpart,
    Primitive,
    HiResPrimitive,
    LowResPrimitive,
    Configuration,
}

/// <summary>A single parsed LDraw file: an MPD section, a library part, or a primitive.</summary>
public sealed class LDrawDocument
{
    public LDrawDocument(
        string name,
        ImmutableArray<LDrawCommand> commands,
        LDrawOrgKind orgKind,
        bool isUnofficial,
        string? description,
        string? originPath)
    {
        Name = name;
        CanonicalName = LDrawName.Canonicalize(name);
        Commands = commands;
        OrgKind = orgKind;
        IsUnofficial = isUnofficial;
        Description = description;
        OriginPath = originPath;
    }

    /// <summary>The name exactly as written, preserved for diagnostics.</summary>
    public string Name { get; }

    /// <summary>Lower-cased, forward-slashed name used for all lookups and comparisons.</summary>
    public string CanonicalName { get; }

    public ImmutableArray<LDrawCommand> Commands { get; }

    public LDrawOrgKind OrgKind { get; }

    /// <summary>True when the <c>!LDRAW_ORG</c> value carried the <c>Unofficial_</c> prefix.</summary>
    public bool IsUnofficial { get; }

    /// <summary>The line-0 description on the first line, if any.</summary>
    public string? Description { get; }

    /// <summary>Where this document came from, for provenance reporting.</summary>
    public string? OriginPath { get; }

    public IEnumerable<SubfileReference> References => Commands.OfType<SubfileReference>();

    public override string ToString() => Name;
}

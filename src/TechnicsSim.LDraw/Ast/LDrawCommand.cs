using System.Numerics;

namespace TechnicsSim.LDraw.Ast;

/// <summary>
/// One parsed LDraw line. Every command retains its source line number so that
/// diagnostics and provenance can point back at the exact origin.
/// </summary>
public abstract record LDrawCommand(int LineNumber)
{
    /// <summary>The LDraw line type (0-5).</summary>
    public abstract int LineType { get; }
}

/// <summary>Line type 0: a comment or meta command.</summary>
public sealed record MetaCommand(int LineNumber, string Text) : LDrawCommand(LineNumber)
{
    public override int LineType => 0;

    /// <summary>
    /// The leading token of the meta, upper-cased (for example <c>!LDRAW_ORG</c> or
    /// <c>BFC</c>), or an empty string for a bare comment.
    /// </summary>
    public string Keyword { get; } = FirstToken(Text);

    /// <summary>Everything after <see cref="Keyword"/>, trimmed.</summary>
    public string Arguments { get; } = Text.Length > FirstToken(Text).Length
        ? Text[FirstToken(Text).Length..].Trim()
        : string.Empty;

    private static string FirstToken(string text)
    {
        var span = text.AsSpan().TrimStart();
        var end = span.IndexOfAny(' ', '\t');
        var token = end < 0 ? span : span[..end];
        return token.ToString().ToUpperInvariant();
    }
}

/// <summary>Line type 1: a reference to another file with a colour and transform.</summary>
public sealed record SubfileReference(
    int LineNumber,
    int Colour,
    Matrix4x4 Transform,
    string TargetName) : LDrawCommand(LineNumber)
{
    public override int LineType => 1;
}

/// <summary>Line type 2: an edge line.</summary>
public sealed record EdgeLine(int LineNumber, int Colour, Vector3 A, Vector3 B)
    : LDrawCommand(LineNumber)
{
    public override int LineType => 2;
}

/// <summary>Line type 3: a triangle.</summary>
public sealed record Triangle(int LineNumber, int Colour, Vector3 A, Vector3 B, Vector3 C)
    : LDrawCommand(LineNumber)
{
    public override int LineType => 3;
}

/// <summary>Line type 4: a quadrilateral.</summary>
public sealed record Quad(int LineNumber, int Colour, Vector3 A, Vector3 B, Vector3 C, Vector3 D)
    : LDrawCommand(LineNumber)
{
    public override int LineType => 4;
}

/// <summary>Line type 5: a camera-dependent optional edge line.</summary>
public sealed record OptionalLine(
    int LineNumber,
    int Colour,
    Vector3 A,
    Vector3 B,
    Vector3 Control1,
    Vector3 Control2) : LDrawCommand(LineNumber)
{
    public override int LineType => 5;
}

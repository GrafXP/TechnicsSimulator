using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using TechnicsSim.LDraw.Ast;

namespace TechnicsSim.LDraw.Parsing;

/// <summary>A line that could not be parsed, kept rather than dropped so reports can show it.</summary>
public sealed record LDrawParseIssue(string DocumentName, int LineNumber, string Line, string Reason);

/// <summary>The result of parsing one physical file, which may hold many MPD sections.</summary>
public sealed record LDrawParseResult(
    ImmutableArray<LDrawDocument> Documents,
    ImmutableArray<LDrawParseIssue> Issues)
{
    /// <summary>The first section, which is the MPD root unless the caller selects another.</summary>
    public LDrawDocument Root => Documents[0];
}

/// <summary>
/// Parses LDraw text into the <see cref="LDrawCommand"/> AST. This is a pure lexical pass:
/// it resolves nothing and expands nothing, so definitions are complete before any
/// instance is expanded.
/// </summary>
public static class LDrawParser
{
    /// <summary>Header keywords that are metadata rather than the file's description line.</summary>
    private static readonly HashSet<string> HeaderKeywords = new(StringComparer.Ordinal)
    {
        "FILE", "NOFILE", "NAME:", "AUTHOR:", "!LDRAW_ORG", "!LICENSE", "!HELP", "!CATEGORY",
        "!KEYWORDS", "!HISTORY", "!CMDLINE", "!THEME", "BFC", "!COLOUR", "!TEXMAP", "!DATA",
    };

    /// <summary>
    /// Parses a file's text. <paramref name="defaultName"/> names the implicit root section
    /// used when the text contains no <c>0 FILE</c> line.
    /// </summary>
    public static LDrawParseResult Parse(string text, string defaultName, string? originPath = null)
    {
        var documents = ImmutableArray.CreateBuilder<LDrawDocument>();
        var issues = ImmutableArray.CreateBuilder<LDrawParseIssue>();

        var currentName = defaultName;
        var currentCommands = ImmutableArray.CreateBuilder<LDrawCommand>();
        var sawFileMeta = false;

        void FlushSection()
        {
            // Content before the first `0 FILE` is only a real section when it holds something.
            if (!sawFileMeta && currentCommands.Count == 0 && documents.Count > 0)
            {
                currentCommands.Clear();
                return;
            }

            documents.Add(BuildDocument(currentName, currentCommands.ToImmutable(), originPath));
            currentCommands.Clear();
        }

        var lineNumber = 0;
        foreach (var rawLine in EnumerateLines(text))
        {
            lineNumber++;
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var command = ParseLine(line, lineNumber, currentName, issues);
            if (command is null)
            {
                continue;
            }

            if (command is MetaCommand meta)
            {
                if (meta.Keyword == "FILE")
                {
                    // A new section begins. Anything accumulated so far is the previous one.
                    if (sawFileMeta || currentCommands.Count > 0)
                    {
                        FlushSection();
                    }

                    sawFileMeta = true;
                    currentName = meta.Arguments;
                    continue;
                }

                if (meta.Keyword == "NOFILE")
                {
                    FlushSection();
                    sawFileMeta = false;
                    currentName = defaultName;
                    continue;
                }
            }

            currentCommands.Add(command);
        }

        if (sawFileMeta || currentCommands.Count > 0 || documents.Count == 0)
        {
            FlushSection();
        }

        return new LDrawParseResult(documents.ToImmutable(), issues.ToImmutable());
    }

    private static LDrawDocument BuildDocument(
        string name,
        ImmutableArray<LDrawCommand> commands,
        string? originPath)
    {
        var orgKind = LDrawOrgKind.Unknown;
        var isUnofficial = false;
        string? description = null;

        foreach (var command in commands)
        {
            if (command is not MetaCommand meta)
            {
                // Headers only appear before geometry; stop as soon as real content starts.
                break;
            }

            if (meta.Keyword == "!LDRAW_ORG")
            {
                (orgKind, isUnofficial) = ParseOrgKind(meta.Arguments);
            }
            else if (description is null && !HeaderKeywords.Contains(meta.Keyword) && meta.Text.Length > 0)
            {
                description = meta.Text;
            }
        }

        return new LDrawDocument(name, commands, orgKind, isUnofficial, description, originPath);
    }

    private static (LDrawOrgKind Kind, bool Unofficial) ParseOrgKind(string arguments)
    {
        var span = arguments.AsSpan().TrimStart();
        var end = span.IndexOfAny(' ', '\t');
        var token = (end < 0 ? span : span[..end]).ToString();

        var unofficial = false;
        foreach (var prefix in new[] { "Unofficial_", "Unofficial" })
        {
            if (token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                unofficial = true;
                token = token[prefix.Length..];
                break;
            }
        }

        var kind = token.ToUpperInvariant() switch
        {
            "MODEL" => LDrawOrgKind.Model,
            "PART" => LDrawOrgKind.Part,
            "SHORTCUT" => LDrawOrgKind.Shortcut,
            "SUBPART" => LDrawOrgKind.Subpart,
            "PRIMITIVE" => LDrawOrgKind.Primitive,
            "48_PRIMITIVE" => LDrawOrgKind.HiResPrimitive,
            "8_PRIMITIVE" => LDrawOrgKind.LowResPrimitive,
            "CONFIGURATION" => LDrawOrgKind.Configuration,
            _ => LDrawOrgKind.Unknown,
        };

        return (kind, unofficial);
    }

    private static LDrawCommand? ParseLine(
        string line,
        int lineNumber,
        string documentName,
        ImmutableArray<LDrawParseIssue>.Builder issues)
    {
        var typeEnd = line.IndexOfAny(WhitespaceChars);
        var typeToken = typeEnd < 0 ? line : line[..typeEnd];

        if (!int.TryParse(typeToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lineType))
        {
            issues.Add(new LDrawParseIssue(documentName, lineNumber, line, "Line does not begin with a line type."));
            return null;
        }

        var rest = typeEnd < 0 ? string.Empty : line[(typeEnd + 1)..].Trim();

        switch (lineType)
        {
            case 0:
                return new MetaCommand(lineNumber, rest);

            case 1:
                return ParseSubfile(line, rest, lineNumber, documentName, issues);

            case 2:
                return ParseGeometry(line, rest, lineNumber, 2, documentName, issues) is { } two
                    ? new EdgeLine(lineNumber, two.Colour, two.Points[0], two.Points[1])
                    : null;

            case 3:
                return ParseGeometry(line, rest, lineNumber, 3, documentName, issues) is { } three
                    ? new Triangle(lineNumber, three.Colour, three.Points[0], three.Points[1], three.Points[2])
                    : null;

            case 4:
                return ParseGeometry(line, rest, lineNumber, 4, documentName, issues) is { } four
                    ? new Quad(lineNumber, four.Colour, four.Points[0], four.Points[1], four.Points[2], four.Points[3])
                    : null;

            case 5:
                return ParseGeometry(line, rest, lineNumber, 4, documentName, issues) is { } five
                    ? new OptionalLine(
                        lineNumber, five.Colour, five.Points[0], five.Points[1], five.Points[2], five.Points[3])
                    : null;

            default:
                issues.Add(new LDrawParseIssue(
                    documentName, lineNumber, line, $"Unknown line type {lineType}."));
                return null;
        }
    }

    private static SubfileReference? ParseSubfile(
        string line,
        string rest,
        int lineNumber,
        string documentName,
        ImmutableArray<LDrawParseIssue>.Builder issues)
    {
        // 1 <colour> x y z a b c d e f g h i <name>. The name may contain spaces, so take
        // exactly 13 numeric fields and treat the entire remainder as the target name.
        Span<Range> fields = stackalloc Range[13];
        if (!TakeFields(rest, fields, out var consumed))
        {
            issues.Add(new LDrawParseIssue(
                documentName, lineNumber, line, "Type-1 line has fewer than 14 fields."));
            return null;
        }

        Span<float> values = stackalloc float[12];
        if (!int.TryParse(rest[fields[0]], NumberStyles.Integer, CultureInfo.InvariantCulture, out var colour)
            && !TryParseDirectColour(rest[fields[0]], out colour))
        {
            issues.Add(new LDrawParseIssue(
                documentName, lineNumber, line, $"Unparsable colour '{rest[fields[0]]}'."));
            return null;
        }

        for (var i = 0; i < 12; i++)
        {
            if (!TryParseNumber(rest[fields[i + 1]], out values[i]))
            {
                issues.Add(new LDrawParseIssue(
                    documentName, lineNumber, line, $"Unparsable number '{rest[fields[i + 1]]}'."));
                return null;
            }
        }

        var targetName = rest[consumed..].Trim();
        if (targetName.Length == 0)
        {
            issues.Add(new LDrawParseIssue(documentName, lineNumber, line, "Type-1 line has no target name."));
            return null;
        }

        // x y z a b c d e f g h i maps to the row-vector matrix documented in PLAN.md:
        //   | a d g 0 |
        //   | b e h 0 |
        //   | c f i 0 |
        //   | x y z 1 |
        var transform = new Matrix4x4(
            values[3], values[6], values[9], 0f,
            values[4], values[7], values[10], 0f,
            values[5], values[8], values[11], 0f,
            values[0], values[1], values[2], 1f);

        return new SubfileReference(lineNumber, colour, transform, targetName);
    }

    private readonly record struct GeometryFields(int Colour, Vector3[] Points);

    private static GeometryFields? ParseGeometry(
        string line,
        string rest,
        int lineNumber,
        int pointCount,
        string documentName,
        ImmutableArray<LDrawParseIssue>.Builder issues)
    {
        var fieldCount = 1 + (pointCount * 3);
        Span<Range> fields = stackalloc Range[16];
        if (!TakeFields(rest, fields[..fieldCount], out _))
        {
            issues.Add(new LDrawParseIssue(
                documentName, lineNumber, line, $"Expected {fieldCount + 1} fields."));
            return null;
        }

        if (!int.TryParse(rest[fields[0]], NumberStyles.Integer, CultureInfo.InvariantCulture, out var colour)
            && !TryParseDirectColour(rest[fields[0]], out colour))
        {
            issues.Add(new LDrawParseIssue(
                documentName, lineNumber, line, $"Unparsable colour '{rest[fields[0]]}'."));
            return null;
        }

        var points = new Vector3[pointCount];
        Span<float> xyz = stackalloc float[3];

        for (var p = 0; p < pointCount; p++)
        {
            for (var c = 0; c < 3; c++)
            {
                if (!TryParseNumber(rest[fields[1 + (p * 3) + c]], out xyz[c]))
                {
                    issues.Add(new LDrawParseIssue(
                        documentName, lineNumber, line,
                        $"Unparsable number '{rest[fields[1 + (p * 3) + c]]}'."));
                    return null;
                }
            }

            points[p] = new Vector3(xyz[0], xyz[1], xyz[2]);
        }

        return new GeometryFields(colour, points);
    }

    private static readonly char[] WhitespaceChars = [' ', '\t'];

    /// <summary>
    /// Splits the first <c>fields.Length</c> whitespace-separated tokens out of
    /// <paramref name="text"/>, reporting where the last one ended so a trailing
    /// space-bearing file name can be taken verbatim.
    /// </summary>
    private static bool TakeFields(string text, Span<Range> fields, out int consumed)
    {
        consumed = 0;
        var index = 0;

        for (var f = 0; f < fields.Length; f++)
        {
            while (index < text.Length && (text[index] == ' ' || text[index] == '\t'))
            {
                index++;
            }

            if (index >= text.Length)
            {
                return false;
            }

            var start = index;
            while (index < text.Length && text[index] != ' ' && text[index] != '\t')
            {
                index++;
            }

            fields[f] = new Range(start, index);
        }

        consumed = index;
        return true;
    }

    private static bool TryParseNumber(ReadOnlySpan<char> token, out float value) =>
        float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    /// <summary>
    /// Direct ("blended") colours are written as <c>0x2RRGGBB</c> or <c>#2RRGGBB</c>.
    /// They are preserved as their integer value; Phase 1 decodes them into RGB.
    /// </summary>
    private static bool TryParseDirectColour(ReadOnlySpan<char> token, out int colour)
    {
        colour = 0;
        ReadOnlySpan<char> digits;
        if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            digits = token[2..];
        }
        else if (token.Length > 0 && token[0] == '#')
        {
            digits = token[1..];
        }
        else
        {
            return false;
        }

        return int.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out colour);
    }

    private static IEnumerable<string> EnumerateLines(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                var end = i > start && text[i - 1] == '\r' ? i - 1 : i;
                yield return text[start..end];
                start = i + 1;
            }
        }

        if (start < text.Length)
        {
            yield return text[start..];
        }
    }
}

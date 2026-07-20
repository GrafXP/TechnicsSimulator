using System.Collections.Immutable;
using TechnicsSim.LDraw.Ast;

namespace TechnicsSim.LDraw.Shadow;

/// <summary>
/// One <c>0 !LDCAD &lt;NAME&gt; [key=value] ...</c> line, kept as ordered raw fields.
///
/// The coverage probe only needs to know which features exist and of what type. The mechanics
/// extractor turns these fields into oriented finite shapes; keeping the ordered raw form here
/// gives both consumers one faithful record rather than two scanners.
/// </summary>
public sealed record ShadowMeta(
    string Name,
    ImmutableArray<KeyValuePair<string, string>> Fields,
    string SourceFile,
    int LineNumber)
{
    /// <summary>Reads a field by key, case-insensitively. Returns null when absent.</summary>
    public string? Field(string key)
    {
        foreach (var pair in Fields)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        return null;
    }

    /// <summary>True for the metas that declare a connection feature.</summary>
    public bool IsSnapFeature => Name is "SNAP_CYL" or "SNAP_CLP" or "SNAP_FGR" or "SNAP_GEN";
}

/// <summary>Extracts LDCad shadow metas from a parsed document.</summary>
public static class ShadowMetaParser
{
    private const string Prefix = "!LDCAD";

    /// <summary>Returns every <c>!LDCAD</c> meta in the document, in file order.</summary>
    public static ImmutableArray<ShadowMeta> Extract(LDrawDocument document, string sourceFile)
    {
        var metas = ImmutableArray.CreateBuilder<ShadowMeta>();

        foreach (var command in document.Commands)
        {
            if (command is not MetaCommand { Keyword: Prefix } meta)
            {
                continue;
            }

            var arguments = meta.Arguments;
            var nameEnd = arguments.IndexOfAny([' ', '\t', '[']);
            var name = (nameEnd < 0 ? arguments : arguments[..nameEnd]).Trim().ToUpperInvariant();
            if (name.Length == 0)
            {
                continue;
            }

            metas.Add(new ShadowMeta(
                name,
                ParseFields(nameEnd < 0 ? string.Empty : arguments[nameEnd..]),
                sourceFile,
                meta.LineNumber));
        }

        return metas.ToImmutable();
    }

    /// <summary>
    /// Parses the bracketed field list. Values may contain spaces (<c>[secs=R 8 2 R 6 16]</c>)
    /// so each field runs to its closing bracket, not to the next space.
    /// </summary>
    private static ImmutableArray<KeyValuePair<string, string>> ParseFields(string text)
    {
        var fields = ImmutableArray.CreateBuilder<KeyValuePair<string, string>>();
        var index = 0;

        while (index < text.Length)
        {
            var open = text.IndexOf('[', index);
            if (open < 0)
            {
                break;
            }

            var close = text.IndexOf(']', open + 1);
            if (close < 0)
            {
                break;
            }

            var body = text[(open + 1)..close];
            var equals = body.IndexOf('=');

            fields.Add(equals < 0
                ? new KeyValuePair<string, string>(body.Trim(), string.Empty)
                : new KeyValuePair<string, string>(body[..equals].Trim(), body[(equals + 1)..].Trim()));

            index = close + 1;
        }

        return fields.ToImmutable();
    }
}

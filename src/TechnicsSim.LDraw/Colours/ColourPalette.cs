using System.Collections.Immutable;
using System.Globalization;
using TechnicsSim.LDraw.Ast;
using TechnicsSim.LDraw.Parsing;
using TechnicsSim.LDraw.Sources;

namespace TechnicsSim.LDraw.Colours;

/// <summary>
/// The colour table from <c>LDConfig.ldr</c>, plus the two inheritance rules that make LDraw
/// colour handling non-obvious:
///
/// <list type="bullet">
/// <item>Code 16 means "use the colour the caller was drawn with".</item>
/// <item>Code 24 means "use the caller's <em>edge</em> colour", and appears on edge lines.</item>
/// </list>
///
/// Direct ("blended") colours encode RGB in the code itself as <c>0x2RRGGBB</c> and are not in
/// the table at all.
/// </summary>
public sealed class ColourPalette
{
    /// <summary>The code meaning "inherit the surface colour from the referencing context".</summary>
    public const int InheritedSurfaceCode = 16;

    /// <summary>The code meaning "inherit the edge colour from the referencing context".</summary>
    public const int InheritedEdgeCode = 24;

    private const int DirectColourFlag = 0x2000000;

    private readonly ImmutableDictionary<int, LDrawColour> _byCode;

    private ColourPalette(ImmutableDictionary<int, LDrawColour> byCode) => _byCode = byCode;

    public int Count => _byCode.Count;

    public IEnumerable<LDrawColour> Colours => _byCode.Values;

    /// <summary>
    /// A minimal palette used when no <c>LDConfig.ldr</c> is available, so tests and the parser
    /// never depend on the external library just to name a colour.
    /// </summary>
    public static ColourPalette Fallback { get; } = new(
        new[]
        {
            new LDrawColour(0, "Black", new Rgba(0x1B, 0x2A, 0x34), new Rgba(0x80, 0x80, 0x80)),
            new LDrawColour(7, "Light_Grey", new Rgba(0x8A, 0x92, 0x8D), new Rgba(0x33, 0x33, 0x33)),
            new LDrawColour(16, "Main_Colour", new Rgba(0x7F, 0x7F, 0x7F), new Rgba(0x33, 0x33, 0x33)),
            new LDrawColour(24, "Edge_Colour", new Rgba(0x33, 0x33, 0x33), new Rgba(0x33, 0x33, 0x33)),
        }.ToImmutableDictionary(c => c.Code));

    /// <summary>Loads the palette from a source, falling back when <c>LDConfig.ldr</c> is absent.</summary>
    public static ColourPalette Load(ILDrawFileSource source)
    {
        foreach (var candidate in new[] { "ldconfig.ldr", "ldraw/ldconfig.ldr" })
        {
            if (source.TryRead(candidate, out var file))
            {
                return Parse(file.Text);
            }
        }

        return Fallback;
    }

    public static ColourPalette Parse(string ldConfigText)
    {
        var parsed = LDrawParser.Parse(ldConfigText, "LDConfig.ldr");
        var builder = ImmutableDictionary.CreateBuilder<int, LDrawColour>();

        foreach (var command in parsed.Root.Commands)
        {
            if (command is MetaCommand { Keyword: "!COLOUR" } meta
                && TryParseColour(meta.Arguments, out var colour))
            {
                builder[colour.Code] = colour;
            }
        }

        return builder.Count == 0 ? Fallback : new ColourPalette(builder.ToImmutable());
    }

    /// <summary>
    /// Parses one <c>!COLOUR</c> body. The format is keyword-driven rather than positional, and
    /// a trailing <c>MATERIAL</c> clause carries its own nested <c>VALUE</c>, so the scan stops
    /// consuming top-level keys once it reaches one.
    /// </summary>
    private static bool TryParseColour(string arguments, out LDrawColour colour)
    {
        colour = null!;
        var tokens = arguments.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2)
        {
            return false;
        }

        var name = tokens[0];
        int? code = null;
        Rgba? value = null;
        Rgba? edge = null;
        var alpha = 255;
        var luminance = 0;
        string? material = null;

        for (var i = 1; i < tokens.Length - 1; i++)
        {
            switch (tokens[i].ToUpperInvariant())
            {
                case "CODE":
                    if (int.TryParse(tokens[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var c))
                    {
                        code = c;
                    }

                    break;

                case "VALUE":
                    if (value is null && Rgba.TryParseHex(tokens[i + 1], out var v))
                    {
                        value = v;
                    }

                    break;

                case "EDGE":
                    if (edge is null && Rgba.TryParseHex(tokens[i + 1], out var e))
                    {
                        edge = e;
                    }

                    break;

                case "ALPHA":
                    _ = int.TryParse(tokens[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out alpha);
                    break;

                case "LUMINANCE":
                    _ = int.TryParse(tokens[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out luminance);
                    break;

                case "MATERIAL":
                    // Everything after MATERIAL belongs to the material clause, including a
                    // second VALUE that must not overwrite the surface colour.
                    material = tokens[i + 1];
                    i = tokens.Length;
                    break;
            }
        }

        if (code is null || value is null)
        {
            return false;
        }

        var surface = value.Value with { A = (byte)Math.Clamp(alpha, 0, 255) };
        colour = new LDrawColour(
            code.Value,
            name,
            surface,
            edge ?? new Rgba(0x33, 0x33, 0x33),
            luminance,
            material);

        return true;
    }

    public bool TryGet(int code, out LDrawColour colour) => _byCode.TryGetValue(code, out colour!);

    /// <summary>True for a <c>0x2RRGGBB</c> direct colour code.</summary>
    public static bool IsDirectColour(int code) => (code & DirectColourFlag) == DirectColourFlag;

    /// <summary>Decodes a direct colour code into RGB.</summary>
    public static Rgba DecodeDirectColour(int code) => new(
        (byte)((code >> 16) & 0xFF),
        (byte)((code >> 8) & 0xFF),
        (byte)(code & 0xFF));

    /// <summary>
    /// Resolves a geometry colour code against the colour its caller was drawn with.
    ///
    /// <paramref name="context"/> is the already-resolved colour of the referencing instance,
    /// which is what codes 16 and 24 inherit from.
    /// </summary>
    public ResolvedColour Resolve(int code, ResolvedColour context)
    {
        if (code == InheritedSurfaceCode)
        {
            return context;
        }

        if (code == InheritedEdgeCode)
        {
            // An edge drawn in the caller's edge colour. Its own edge colour stays the same so
            // that a further nested inherit does not drift.
            return new ResolvedColour(InheritedEdgeCode, context.Edge, context.Edge);
        }

        if (IsDirectColour(code))
        {
            var direct = DecodeDirectColour(code);
            return new ResolvedColour(code, direct, direct);
        }

        return TryGet(code, out var colour)
            ? new ResolvedColour(code, colour.Value, colour.Edge)
            : new ResolvedColour(code, new Rgba(0x7F, 0x7F, 0x7F), new Rgba(0x33, 0x33, 0x33));
    }

    /// <summary>The colour a top-level instance starts from when the model names no colour.</summary>
    public ResolvedColour DefaultContext => Resolve(InheritedSurfaceCode, new ResolvedColour(
        InheritedSurfaceCode, new Rgba(0x7F, 0x7F, 0x7F), new Rgba(0x33, 0x33, 0x33)));
}

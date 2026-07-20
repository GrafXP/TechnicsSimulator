namespace TechnicsSim.LDraw.Colours;

/// <summary>A straight 8-bit-per-channel colour with alpha, in sRGB.</summary>
public readonly record struct Rgba(byte R, byte G, byte B, byte A = 255)
{
    /// <summary>Parses <c>#RRGGBB</c> or <c>#AARRGGBB</c>. Returns false on anything else.</summary>
    public static bool TryParseHex(ReadOnlySpan<char> text, out Rgba colour)
    {
        colour = default;
        if (text.Length > 0 && text[0] == '#')
        {
            text = text[1..];
        }

        if (text.Length is not (6 or 8))
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[4];
        var offset = text.Length == 8 ? 0 : 1;
        bytes[0] = 255;

        for (var i = 0; i < text.Length / 2; i++)
        {
            if (!byte.TryParse(text.Slice(i * 2, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
            {
                return false;
            }

            bytes[i + offset] = b;
        }

        colour = new Rgba(bytes[1], bytes[2], bytes[3], bytes[0]);
        return true;
    }

    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}{(A == 255 ? string.Empty : $"/{A:X2}")}";
}

/// <summary>One entry from <c>LDConfig.ldr</c>.</summary>
/// <param name="Luminance">Non-zero for glow-in-the-dark colours.</param>
/// <param name="Material">The <c>MATERIAL</c> token (GLITTER, SPECKLE) when present.</param>
public sealed record LDrawColour(
    int Code,
    string Name,
    Rgba Value,
    Rgba Edge,
    int Luminance = 0,
    string? Material = null)
{
    public bool IsTranslucent => Value.A < 255;
}

/// <summary>
/// The resolved appearance of a piece of geometry: a surface colour plus the edge colour
/// that LDraw pairs with it.
/// </summary>
public readonly record struct ResolvedColour(int Code, Rgba Value, Rgba Edge)
{
    public bool IsTranslucent => Value.A < 255;
}

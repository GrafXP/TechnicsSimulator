namespace TechnicsSim.LDraw;

/// <summary>
/// LDraw names are compared case-insensitively and may use either slash direction.
/// Canonicalization normalizes both, but callers keep the original spelling for
/// diagnostics so error messages quote what the file actually said.
/// </summary>
public static class LDrawName
{
    /// <summary>Lower-cases and forward-slashes a name, collapsing redundant separators.</summary>
    public static string Canonicalize(string name)
    {
        var trimmed = name.Trim();
        Span<char> buffer = trimmed.Length <= 256 ? stackalloc char[trimmed.Length] : new char[trimmed.Length];
        var length = 0;
        var previousWasSlash = false;

        foreach (var c in trimmed)
        {
            var normalized = c == '\\' ? '/' : char.ToLowerInvariant(c);
            if (normalized == '/')
            {
                // Leading and doubled separators carry no meaning in LDraw references.
                if (previousWasSlash || length == 0)
                {
                    continue;
                }

                previousWasSlash = true;
            }
            else
            {
                previousWasSlash = false;
            }

            buffer[length++] = normalized;
        }

        return new string(buffer[..length]);
    }

    /// <summary>The file name without any directory prefix, canonicalized.</summary>
    public static string CanonicalLeaf(string name)
    {
        var canonical = Canonicalize(name);
        var slash = canonical.LastIndexOf('/');
        return slash < 0 ? canonical : canonical[(slash + 1)..];
    }
}

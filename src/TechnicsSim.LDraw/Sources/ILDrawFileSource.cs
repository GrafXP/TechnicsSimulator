using System.Diagnostics.CodeAnalysis;

namespace TechnicsSim.LDraw.Sources;

/// <summary>The text of one file plus enough provenance to explain where it came from.</summary>
/// <param name="CanonicalPath">The library-relative path that matched, e.g. <c>parts/3705.dat</c>.</param>
/// <param name="OriginPath">A human-readable absolute or archive-qualified location.</param>
/// <param name="SourceName">The <see cref="ILDrawFileSource.DisplayName"/> that supplied it.</param>
public sealed record LDrawSourceFile(
    string CanonicalPath,
    string OriginPath,
    string SourceName,
    string Text);

/// <summary>
/// A read-only provider of LDraw file text keyed by library-relative canonical path.
/// Implementations exist for a directory, a ZIP archive, and an in-memory set so that a
/// 480+ MB library never has to be extracted just to read a few hundred parts.
/// </summary>
public interface ILDrawFileSource : IDisposable
{
    /// <summary>Short label shown in reports and diagnostics.</summary>
    string DisplayName { get; }

    /// <summary>True when this source can supply the given canonical library-relative path.</summary>
    bool Contains(string canonicalPath);

    /// <summary>Reads a file, returning false when this source does not have it.</summary>
    bool TryRead(string canonicalPath, [NotNullWhen(true)] out LDrawSourceFile? file);

    /// <summary>All canonical paths this source can supply. Used for library statistics.</summary>
    IEnumerable<string> EnumeratePaths();
}

using System.Collections.Immutable;
using System.Numerics;
using TechnicsSim.LDraw.Ast;
using TechnicsSim.LDraw.Resolution;

namespace TechnicsSim.LDraw.Expansion;

/// <summary>
/// Expands a model into logical part instances.
///
/// This traversal descends through submodels and stops at logical part boundaries. It is
/// deliberately distinct from geometry expansion, which continues down into subparts and
/// primitives. Conflating the two is what produces the misleading claim that 8275 contains
/// tens of thousands of parts: it contains 3,029 logical instances, 1,630 of which are the
/// embedded LS70 track link whose own primitive tree is geometry, not parts.
/// </summary>
public sealed class LogicalPartExpander
{
    /// <summary>The colour code meaning "inherit from the referencing context".</summary>
    private const int InheritedColour = 16;

    private readonly LDrawResolver _resolver;

    public LogicalPartExpander(LDrawResolver resolver) => _resolver = resolver;

    public ModelExpansion Expand(LDrawDocument root, int defaultColour = InheritedColour)
    {
        var instances = ImmutableArray.CreateBuilder<LogicalPartInstance>();
        var unresolved = ImmutableArray.CreateBuilder<UnresolvedReference>();
        var ambiguous = ImmutableArray.CreateBuilder<string>();
        var nonPart = ImmutableArray.CreateBuilder<NonPartReference>();
        var submodelReferences = 0;
        var inlineGeometry = new Dictionary<int, int>();

        var activeStack = new HashSet<string>(StringComparer.Ordinal);

        void Descend(LDrawDocument document, Matrix4x4 transform, int colour, string idPrefix, int depth, string chain)
        {
            if (!activeStack.Add(document.CanonicalName))
            {
                // Already being expanded further up the stack: a genuine cycle.
                unresolved.Add(new UnresolvedReference(
                    document.Name, ResolutionFailure.Cyclic, chain, document.Name, 0));
                return;
            }

            try
            {
                foreach (var command in document.Commands)
                {
                    if (command is not SubfileReference reference)
                    {
                        if (command.LineType is 2 or 3 or 4 or 5)
                        {
                            // Geometry written directly into a model section: the generated
                            // hose and spring fallback meshes, most heavily in 42055.
                            inlineGeometry[command.LineType] =
                                inlineGeometry.GetValueOrDefault(command.LineType) + 1;
                        }

                        continue;
                    }

                    var resolved = _resolver.Resolve(reference.TargetName);
                    var childChain = $"{chain} -> {reference.TargetName} (line {reference.LineNumber})";

                    if (!resolved.IsResolved)
                    {
                        unresolved.Add(new UnresolvedReference(
                            reference.TargetName, resolved.Failure, childChain,
                            document.Name, reference.LineNumber));
                        continue;
                    }

                    if (!resolved.AmbiguousAlternatives.IsDefaultOrEmpty)
                    {
                        ambiguous.Add(
                            $"{reference.TargetName}: chose {resolved.OriginPath}, also in "
                            + string.Join(", ", resolved.AmbiguousAlternatives));
                    }

                    var child = resolved.Document!;
                    var kind = LogicalClassifier.Classify(child, resolved.Origin);

                    // Row-vector convention: apply the child transform, then the parent's.
                    var childTransform = Matrix4x4.Multiply(reference.Transform, transform);
                    var childColour = reference.Colour == InheritedColour ? colour : reference.Colour;
                    var childId = idPrefix.Length == 0
                        ? $"{document.Name}@{reference.LineNumber}"
                        : $"{idPrefix}|{document.Name}@{reference.LineNumber}";

                    if (kind == LogicalKind.Model)
                    {
                        submodelReferences++;
                        Descend(child, childTransform, childColour, childId, depth + 1, childChain);
                        continue;
                    }

                    // The logical part boundary is the first non-model document reached while
                    // descending. Usually that is a part or shortcut. Occasionally a model
                    // places a subpart or primitive directly -- 8275 positions the Power
                    // Functions ribbon-cable end `s\58124s03.dat` next to its motor -- and
                    // that is still an independently placed object with its own pose, not
                    // geometry belonging to some enclosing part. It gets an instance, and the
                    // unusual classification is recorded so reports can show it.
                    instances.Add(new LogicalPartInstance(
                        childId,
                        reference.TargetName,
                        child.CanonicalName,
                        childTransform,
                        childColour,
                        depth,
                        resolved.Origin,
                        resolved.OriginPath));

                    if (kind != LogicalKind.Part)
                    {
                        nonPart.Add(new NonPartReference(
                            reference.TargetName, child.CanonicalName, kind, resolved.Origin,
                            document.Name, reference.LineNumber));
                    }
                }
            }
            finally
            {
                activeStack.Remove(document.CanonicalName);
            }
        }

        Descend(root, Matrix4x4.Identity, defaultColour, string.Empty, 0, root.Name);

        return new ModelExpansion(
            root,
            instances.ToImmutable(),
            unresolved.ToImmutable(),
            ambiguous.ToImmutable(),
            nonPart.ToImmutable(),
            submodelReferences,
            inlineGeometry.ToImmutableDictionary());
    }
}

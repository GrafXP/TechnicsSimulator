using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using TechnicsSim.LDraw;
using TechnicsSim.LDraw.Ast;
using TechnicsSim.LDraw.Resolution;
using TechnicsSim.LDraw.Shadow;
using TechnicsSim.LDraw.Sources;

namespace TechnicsSim.Mechanics.Features;

/// <summary>
/// Replays LDCad's effective shadow-patch semantics over the official geometry tree.
/// Results are cached in canonical part coordinates and can then be placed on model instances.
/// </summary>
public sealed class EffectiveFeatureExtractor
{
    private const float TransformTolerance = 1e-3f;

    private readonly LDrawResolver _resolver;
    private readonly ILDrawFileSource _shadowSource;
    private readonly Dictionary<string, PartFeatureExtraction> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LoadedShadow?> _shadowCache = new(StringComparer.Ordinal);
    private readonly HashSet<string> _active = new(StringComparer.Ordinal);

    public EffectiveFeatureExtractor(LDrawResolver resolver, ILDrawFileSource shadowSource)
    {
        _resolver = resolver;
        _shadowSource = shadowSource;
    }

    public PartFeatureExtraction Extract(string canonicalPartName)
    {
        var canonical = LDrawName.Canonicalize(canonicalPartName);
        if (_cache.TryGetValue(canonical, out var cached))
        {
            return cached;
        }

        if (!_active.Add(canonical))
        {
            return new PartFeatureExtraction(
                canonical,
                [],
                [new FeatureExtractionIssue(canonical, null, 0, "Cyclic official geometry reference.")],
                []);
        }

        try
        {
            var result = ExtractUncached(canonical);
            _cache[canonical] = result;
            return result;
        }
        finally
        {
            _active.Remove(canonical);
        }
    }

    private sealed record AccumulatedFeature(EffectiveFeature Feature, bool InheritedAtThisLevel);

    private sealed record LoadedShadow(string Path, ImmutableArray<ShadowMeta> Metas);

    private PartFeatureExtraction ExtractUncached(string canonical)
    {
        var features = new List<AccumulatedFeature>();
        var issues = ImmutableArray.CreateBuilder<FeatureExtractionIssue>();
        var rejected = ImmutableArray.CreateBuilder<RejectedFeatureInheritance>();
        var resolved = _resolver.Resolve(canonical);

        if (!resolved.IsResolved)
        {
            issues.Add(new FeatureExtractionIssue(canonical, null, 0, "Official part could not be resolved."));
            return new PartFeatureExtraction(canonical, [], issues.ToImmutable(), []);
        }

        var document = resolved.Document!;

        // Official references precede the appended shadow patch. Each reference site gets its
        // own transformed copy; caching a child never collapses repeated geometry occurrences.
        foreach (var reference in document.References)
        {
            var childResolved = _resolver.Resolve(reference.TargetName);
            if (!childResolved.IsResolved)
            {
                issues.Add(new FeatureExtractionIssue(
                    canonical, null, reference.LineNumber,
                    $"Referenced official file '{reference.TargetName}' could not be resolved."));
                continue;
            }

            var child = Extract(childResolved.Document!.CanonicalName);
            issues.AddRange(child.Issues);
            rejected.AddRange(child.RejectedInheritance);

            foreach (var childFeature in child.Features)
            {
                if (!TryInherit(
                        canonical, document.Name, reference, childFeature,
                        out var inherited, out var rejectionReason))
                {
                    rejected.Add(new RejectedFeatureInheritance(
                        canonical, childFeature.Key, document.Name, reference.LineNumber, rejectionReason!));
                    continue;
                }

                features.Add(new AccumulatedFeature(inherited!, true));
            }
        }

        if (LoadShadow(canonical) is { } shadow)
        {
            ApplyPatch(document, shadow, features, issues);
        }

        return new PartFeatureExtraction(
            canonical,
            features.Select(item => item.Feature).ToImmutableArray(),
            issues.Distinct().ToImmutableArray(),
            rejected.Distinct().ToImmutableArray());
    }

    private void ApplyPatch(
        LDrawDocument official,
        LoadedShadow shadow,
        List<AccumulatedFeature> features,
        ImmutableArray<FeatureExtractionIssue>.Builder issues)
    {
        foreach (var meta in shadow.Metas)
        {
            switch (meta.Name)
            {
                case "SNAP_CLEAR":
                    var id = EmptyToNull(meta.Field("id"));
                    features.RemoveAll(item =>
                        item.InheritedAtThisLevel
                        && (id is null || string.Equals(item.Feature.Id, id, StringComparison.Ordinal)));
                    break;

                case "SNAP_INCL":
                    ApplyInclude(official, shadow, meta, features, issues);
                    break;

                case "SNAP_CYL":
                case "SNAP_CLP":
                case "SNAP_FGR":
                case "SNAP_GEN":
                    foreach (var feature in ParseFeature(official.CanonicalName, meta, FeatureOrigin.Direct, issues))
                    {
                        features.Add(new AccumulatedFeature(feature, false));
                    }

                    break;
            }
        }
    }

    private void ApplyInclude(
        LDrawDocument official,
        LoadedShadow containingShadow,
        ShadowMeta include,
        List<AccumulatedFeature> features,
        ImmutableArray<FeatureExtractionIssue>.Builder issues)
    {
        var reference = EmptyToNull(include.Field("ref"));
        if (reference is null)
        {
            issues.Add(Issue(official.CanonicalName, include, "SNAP_INCL has no ref field."));
            return;
        }

        if (!IsAllowedLocalReference(reference))
        {
            issues.Add(Issue(official.CanonicalName, include, $"SNAP_INCL ref '{reference}' is not a local shadow reference."));
            return;
        }

        var includedShadow = LoadIncludedShadow(containingShadow.Path, reference);
        if (includedShadow is null)
        {
            issues.Add(Issue(official.CanonicalName, include, $"SNAP_INCL ref '{reference}' was not found."));
            return;
        }

        if (!TryParsePlacement(include, out var includeBase, out var placementReason))
        {
            issues.Add(Issue(official.CanonicalName, include, placementReason!));
            return;
        }

        if (!TryVector(include.Field("scale"), Vector3.One, out var includeScale)
            || Math.Abs(includeScale.X * includeScale.Y * includeScale.Z) < 1e-8f)
        {
            issues.Add(Issue(official.CanonicalName, include, $"Invalid include scale value '{include.Field("scale")}'."));
            return;
        }

        if (!TryParseGrid(include.Field("grid"), out var grid, out var gridReason))
        {
            issues.Add(Issue(official.CanonicalName, include, gridReason!));
            return;
        }

        var includeId = EmptyToNull(include.Field("id"));
        var includedIndex = 0;

        // Includes are deliberately non-recursive: only feature declarations in the target
        // shadow file are read. SNAP_INCL lines inside it are ignored.
        foreach (var meta in includedShadow.Metas.Where(meta => meta.IsSnapFeature))
        {
            foreach (var sourceFeature in ParseFeature(official.CanonicalName, meta, FeatureOrigin.Included, issues))
            {
                foreach (var offset in grid)
                {
                    // Scale the included shape itself, then apply grid spacing in the include's
                    // local plane, then orientation/position. Grid spacing is not scaled.
                    var placement = Matrix4x4.Multiply(
                        Matrix4x4.Multiply(Matrix4x4.CreateScale(includeScale), Matrix4x4.CreateTranslation(offset)),
                        includeBase);
                    var transform = Matrix4x4.Multiply(sourceFeature.Transform, placement);
                    var step = new FeatureTransformStep(
                        containingShadow.Path,
                        include.LineNumber,
                        "SNAP_INCL non-recursive placement",
                        placement);

                    features.Add(new AccumulatedFeature(
                        sourceFeature with
                        {
                            Key = $"{containingShadow.Path}:{include.LineNumber}/include/{sourceFeature.Key}#{includedIndex++}",
                            Id = includeId ?? sourceFeature.Id,
                            Transform = transform,
                            Origin = FeatureOrigin.Included,
                            Provenance = sourceFeature.Provenance with
                            {
                                OfficialFile = official.CanonicalName,
                                TransformChain = sourceFeature.Provenance.TransformChain.Add(step),
                            },
                        },
                        false));
                }
            }
        }
    }

    private static bool TryInherit(
        string containingPart,
        string declaringFile,
        SubfileReference reference,
        EffectiveFeature feature,
        out EffectiveFeature? inherited,
        out string? reason)
    {
        inherited = null;
        reason = null;

        var beforeX = Vector3.TransformNormal(Vector3.UnitX, feature.Transform);
        var beforeY = Vector3.TransformNormal(Vector3.UnitY, feature.Transform);
        var beforeZ = Vector3.TransformNormal(Vector3.UnitZ, feature.Transform);
        var afterX = Vector3.TransformNormal(beforeX, reference.Transform);
        var afterY = Vector3.TransformNormal(beforeY, reference.Transform);
        var afterZ = Vector3.TransformNormal(beforeZ, reference.Transform);

        var sx = Ratio(afterX.Length(), beforeX.Length());
        var sy = Ratio(afterY.Length(), beforeY.Length());
        var sz = Ratio(afterZ.Length(), beforeZ.Length());
        var scaled = !Near(sx, 1f) || !Near(sy, 1f) || !Near(sz, 1f) || !Orthogonal(afterX, afterY, afterZ);

        if (scaled && !AcceptsScale(feature.ScalePolicy, sx, sy, sz, afterX, afterY, afterZ))
        {
            reason = $"Scale policy {feature.ScalePolicy} rejected feature-basis scale ({sx:G4}, {sy:G4}, {sz:G4}).";
            return false;
        }

        var mirrored = reference.Transform.GetDeterminant() < 0f;
        if (mirrored && feature.MirrorPolicy == MirrorInheritancePolicy.None)
        {
            reason = "Mirror policy None rejected a reflected official reference.";
            return false;
        }

        var transform = Matrix4x4.Multiply(feature.Transform, reference.Transform);
        var key = $"{declaringFile}@{reference.LineNumber}/{feature.Key}";
        var rule = mirrored
            ? "Official child inheritance; mirror corrected by reflected radius basis"
            : "Official child feature inheritance";
        var step = new FeatureTransformStep(declaringFile, reference.LineNumber, rule, reference.Transform);

        inherited = feature with
        {
            Key = key,
            Transform = transform,
            Origin = FeatureOrigin.Inherited,
            Provenance = feature.Provenance with
            {
                OfficialFile = containingPart,
                TransformChain = feature.Provenance.TransformChain.Add(step),
            },
        };
        return true;
    }

    private static bool AcceptsScale(
        ScaleInheritancePolicy policy,
        float sx,
        float sy,
        float sz,
        Vector3 x,
        Vector3 y,
        Vector3 z)
    {
        if (!Orthogonal(x, y, z))
        {
            return false;
        }

        return policy switch
        {
            ScaleInheritancePolicy.None => false,
            ScaleInheritancePolicy.YOnly => Near(sx, 1f) && Near(sz, 1f),
            ScaleInheritancePolicy.RadiusOnly => Near(sy, 1f) && Near(sx, sz),
            ScaleInheritancePolicy.YAndRadius => Near(sx, sz),
            _ => false,
        };
    }

    private static float Ratio(float after, float before) => before < 1e-6f ? float.PositiveInfinity : after / before;

    private static bool Orthogonal(Vector3 x, Vector3 y, Vector3 z)
    {
        if (x.LengthSquared() < 1e-10f || y.LengthSquared() < 1e-10f || z.LengthSquared() < 1e-10f)
        {
            return false;
        }

        x = Vector3.Normalize(x);
        y = Vector3.Normalize(y);
        z = Vector3.Normalize(z);
        return Math.Abs(Vector3.Dot(x, y)) <= TransformTolerance
            && Math.Abs(Vector3.Dot(x, z)) <= TransformTolerance
            && Math.Abs(Vector3.Dot(y, z)) <= TransformTolerance;
    }

    private static bool Near(float left, float right) => Math.Abs(left - right) <= TransformTolerance;

    private IEnumerable<EffectiveFeature> ParseFeature(
        string officialFile,
        ShadowMeta meta,
        FeatureOrigin origin,
        ImmutableArray<FeatureExtractionIssue>.Builder issues)
    {
        if (!TryParsePlacement(meta, out var placement, out var placementReason))
        {
            issues.Add(Issue(officialFile, meta, placementReason!));
            yield break;
        }

        if (!TryParseGrid(meta.Field("grid"), out var grid, out var gridReason))
        {
            issues.Add(Issue(officialFile, meta, gridReason!));
            yield break;
        }

        if (!TryParseShape(meta, out var shape, out var shapeReason))
        {
            issues.Add(Issue(officialFile, meta, shapeReason!));
            yield break;
        }

        var scalePolicy = ParseScalePolicy(meta.Field("scale"));
        if (scalePolicy is null)
        {
            issues.Add(Issue(officialFile, meta, $"Unknown scale policy '{meta.Field("scale")}'."));
            yield break;
        }

        var mirrorPolicy = ParseMirrorPolicy(meta.Field("mirror"), meta.Name == "SNAP_CYL");
        if (mirrorPolicy is null)
        {
            issues.Add(Issue(officialFile, meta, $"Unknown mirror policy '{meta.Field("mirror")}'."));
            yield break;
        }

        var index = 0;
        foreach (var offset in grid)
        {
            var gridPlacement = Matrix4x4.Multiply(Matrix4x4.CreateTranslation(offset), placement);
            var key = $"{meta.SourceFile}:{meta.LineNumber}#{index++}";
            var rule = meta.Name switch
            {
                "SNAP_CYL" => "LDCad SNAP_CYL finite section profile",
                "SNAP_CLP" => "LDCad SNAP_CLP finite clip",
                "SNAP_FGR" => "LDCad SNAP_FGR finger sequence",
                "SNAP_GEN" => "LDCad SNAP_GEN typed bounds",
                _ => "LDCad snap feature",
            };

            yield return new EffectiveFeature(
                key,
                EmptyToNull(meta.Field("id")),
                EmptyToNull(meta.Field("group")),
                gridPlacement,
                scalePolicy.Value,
                mirrorPolicy.Value,
                shape!,
                origin,
                new FeatureProvenance(
                    officialFile,
                    meta.SourceFile,
                    meta.LineNumber,
                    rule,
                    [new FeatureTransformStep(meta.SourceFile, meta.LineNumber, "Feature pos/ori/grid", gridPlacement)]));
        }
    }

    private static bool TryParseShape(ShadowMeta meta, out SnapShape? shape, out string? reason)
    {
        shape = null;
        reason = null;

        switch (meta.Name)
        {
            case "SNAP_CYL":
                if (!TryParseGender(meta.Field("gender"), out var cylinderGender))
                {
                    reason = $"Invalid cylinder gender '{meta.Field("gender")}'.";
                    return false;
                }

                if (!TryParseSections(meta.Field("secs"), out var sections, out reason))
                {
                    return false;
                }

                if (!TryParseCaps(meta.Field("caps"), out var caps))
                {
                    reason = $"Invalid caps value '{meta.Field("caps")}'.";
                    return false;
                }

                shape = new CylinderSnapShape(
                    cylinderGender, sections, caps,
                    ParseBool(meta.Field("center")), ParseBool(meta.Field("slide")));
                return true;

            case "SNAP_CLP":
                if (!TryFloat(meta.Field("radius"), 4f, out var clipRadius)
                    || !TryFloat(meta.Field("length"), 8f, out var clipLength)
                    || clipRadius < 0f || clipLength < 0f)
                {
                    reason = "Invalid SNAP_CLP radius or length.";
                    return false;
                }

                shape = new ClipSnapShape(
                    clipRadius, clipLength,
                    ParseBool(meta.Field("center")), ParseBool(meta.Field("slide")));
                return true;

            case "SNAP_FGR":
                if (!TryParseGender(meta.Field("genderOfs"), out var fingerGender)
                    || !TryFloatList(meta.Field("seq"), out var sequence)
                    || sequence.IsEmpty
                    || !TryFloat(meta.Field("radius"), 0f, out var fingerRadius))
                {
                    reason = "Invalid SNAP_FGR genderOfs, seq, or radius.";
                    return false;
                }

                shape = new FingerSnapShape(
                    fingerGender, sequence, fingerRadius, ParseBool(meta.Field("center")));
                return true;

            case "SNAP_GEN":
                if (!TryParseGender(meta.Field("gender"), out var genericGender)
                    || !TryParseGenericBounds(meta.Field("bounding"), out var kind, out var extents))
                {
                    reason = "Invalid SNAP_GEN gender or bounding value.";
                    return false;
                }

                shape = new GenericSnapShape(genericGender, kind, extents);
                return true;

            default:
                reason = $"Unsupported snap meta {meta.Name}.";
                return false;
        }
    }

    private static bool TryParseSections(
        string? text,
        out ImmutableArray<CylinderSection> sections,
        out string? reason)
    {
        var result = ImmutableArray.CreateBuilder<CylinderSection>();
        reason = null;
        var tokens = Tokens(text);
        if (tokens.Length == 0 || tokens.Length % 3 != 0)
        {
            sections = [];
            reason = "Cylinder secs must contain shape/radius/length triples.";
            return false;
        }

        for (var i = 0; i < tokens.Length; i += 3)
        {
            var kind = tokens[i].ToUpperInvariant() switch
            {
                "R" => CylinderSectionKind.Round,
                "A" => CylinderSectionKind.Axle,
                "S" => CylinderSectionKind.Square,
                "_L" => CylinderSectionKind.FlexiblePrevious,
                "L_" => CylinderSectionKind.FlexibleNext,
                _ => (CylinderSectionKind?)null,
            };

            if (kind is null
                || !TryFloat(tokens[i + 1], 0f, out var radius)
                || !TryFloat(tokens[i + 2], 0f, out var length)
                || radius < 0f || length < 0f)
            {
                sections = [];
                reason = $"Invalid cylinder section near token {i + 1}.";
                return false;
            }

            result.Add(new CylinderSection(kind.Value, radius, length));
        }

        sections = result.ToImmutable();
        return true;
    }

    private static bool TryParsePlacement(
        ShadowMeta meta,
        out Matrix4x4 placement,
        out string? reason)
    {
        placement = Matrix4x4.Identity;
        reason = null;

        if (!TryVector(meta.Field("pos"), Vector3.Zero, out var position))
        {
            reason = $"Invalid pos value '{meta.Field("pos")}'.";
            return false;
        }

        if (!TryOrientation(meta.Field("ori"), out var orientation))
        {
            reason = $"Invalid ori value '{meta.Field("ori")}'.";
            return false;
        }

        orientation.M41 = position.X;
        orientation.M42 = position.Y;
        orientation.M43 = position.Z;

        if (Math.Abs(orientation.GetDeterminant()) < 1e-8f)
        {
            reason = "Feature pos/ori matrix is singular.";
            return false;
        }

        placement = orientation;
        return true;
    }

    private static bool TryOrientation(string? text, out Matrix4x4 result)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            result = Matrix4x4.Identity;
            return true;
        }

        var tokens = Tokens(text);
        if (tokens.Length != 9)
        {
            result = default;
            return false;
        }

        Span<float> value = stackalloc float[9];
        for (var i = 0; i < value.Length; i++)
        {
            if (!TryFloat(tokens[i], 0f, out value[i]))
            {
                result = default;
                return false;
            }
        }

        // Same row-vector mapping as an LDraw type-1 matrix.
        result = new Matrix4x4(
            value[0], value[3], value[6], 0f,
            value[1], value[4], value[7], 0f,
            value[2], value[5], value[8], 0f,
            0f, 0f, 0f, 1f);
        return true;
    }

    private static bool TryParseGrid(string? text, out ImmutableArray<Vector3> offsets, out string? reason)
    {
        reason = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            offsets = [Vector3.Zero];
            return true;
        }

        var tokens = Tokens(text);
        if (TryParseGridDimensions(tokens, 3, out offsets)
            || TryParseGridDimensions(tokens, 2, out offsets))
        {
            return true;
        }

        offsets = [];
        reason = $"Invalid grid value '{text}'.";
        return false;
    }

    private readonly record struct GridDimension(int Count, bool Centered, float Step);

    private static bool TryParseGridDimensions(
        string[] tokens,
        int dimensionCount,
        out ImmutableArray<Vector3> offsets)
    {
        var cursor = 0;
        Span<(int Count, bool Centered)> counts = stackalloc (int, bool)[3];
        for (var dimension = 0; dimension < dimensionCount; dimension++)
        {
            var centered = TakeCentre(tokens, ref cursor);
            if (!TakeInt(tokens, ref cursor, out var count) || count < 1)
            {
                offsets = [];
                return false;
            }

            counts[dimension] = (count, centered);
        }

        if (cursor + dimensionCount != tokens.Length)
        {
            offsets = [];
            return false;
        }

        Span<GridDimension> dimensions = stackalloc GridDimension[3];
        for (var dimension = 0; dimension < dimensionCount; dimension++)
        {
            if (!TryFloat(tokens[cursor + dimension], 0f, out var step))
            {
                offsets = [];
                return false;
            }

            dimensions[dimension] = new GridDimension(counts[dimension].Count, counts[dimension].Centered, step);
        }

        // The documented legacy form is X/Z. Production shadows also use an extended X/Y/Z
        // form (three counts followed by three steps), notably for actuator mounting holes.
        var x = dimensions[0];
        var y = dimensionCount == 3 ? dimensions[1] : new GridDimension(1, false, 0f);
        var z = dimensionCount == 3 ? dimensions[2] : dimensions[1];
        var result = ImmutableArray.CreateBuilder<Vector3>(x.Count * y.Count * z.Count);
        var startX = x.Centered ? -(x.Count - 1) * x.Step * 0.5f : 0f;
        var startY = y.Centered ? -(y.Count - 1) * y.Step * 0.5f : 0f;
        var startZ = z.Centered ? -(z.Count - 1) * z.Step * 0.5f : 0f;

        for (var zIndex = 0; zIndex < z.Count; zIndex++)
        {
            for (var yIndex = 0; yIndex < y.Count; yIndex++)
            {
                for (var xIndex = 0; xIndex < x.Count; xIndex++)
                {
                    result.Add(new Vector3(
                        startX + (xIndex * x.Step),
                        startY + (yIndex * y.Step),
                        startZ + (zIndex * z.Step)));
                }
            }
        }

        offsets = result.ToImmutable();
        return true;
    }

    private static bool TakeCentre(string[] tokens, ref int cursor)
    {
        if (cursor < tokens.Length && string.Equals(tokens[cursor], "C", StringComparison.OrdinalIgnoreCase))
        {
            cursor++;
            return true;
        }

        return false;
    }

    private static bool TakeInt(string[] tokens, ref int cursor, out int value)
    {
        if (cursor < tokens.Length
            && int.TryParse(tokens[cursor], NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            cursor++;
            return true;
        }

        value = 0;
        return false;
    }

    private static ScaleInheritancePolicy? ParseScalePolicy(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Equals("none", StringComparison.OrdinalIgnoreCase)
            ? ScaleInheritancePolicy.None
            : value.ToUpperInvariant() switch
            {
                "YONLY" => ScaleInheritancePolicy.YOnly,
                "RONLY" => ScaleInheritancePolicy.RadiusOnly,
                "YANDR" => ScaleInheritancePolicy.YAndRadius,
                _ => null,
            };

    private static MirrorInheritancePolicy? ParseMirrorPolicy(string? value, bool cylinderDefault)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return cylinderDefault ? MirrorInheritancePolicy.Correct : MirrorInheritancePolicy.None;
        }

        return value.ToUpperInvariant() switch
        {
            "NONE" => MirrorInheritancePolicy.None,
            "COR" => MirrorInheritancePolicy.Correct,
            _ => null,
        };
    }

    private static bool TryParseCaps(string? value, out SnapCaps caps)
    {
        caps = string.IsNullOrWhiteSpace(value) ? SnapCaps.One : value.ToUpperInvariant() switch
        {
            "NONE" => SnapCaps.None,
            "ONE" => SnapCaps.One,
            "TWO" => SnapCaps.Two,
            "A" => SnapCaps.A,
            "B" => SnapCaps.B,
            _ => (SnapCaps)(-1),
        };
        return (int)caps >= 0;
    }

    private static bool TryParseGender(string? value, out SnapGender gender)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Equals("M", StringComparison.OrdinalIgnoreCase)
            || value.Equals("male", StringComparison.OrdinalIgnoreCase))
        {
            gender = SnapGender.Male;
            return true;
        }

        if (value.Equals("F", StringComparison.OrdinalIgnoreCase)
            || value.Equals("female", StringComparison.OrdinalIgnoreCase))
        {
            gender = SnapGender.Female;
            return true;
        }

        gender = default;
        return false;
    }

    private static bool TryParseGenericBounds(
        string? text,
        out GenericBoundsKind kind,
        out Vector3 extents)
    {
        var tokens = Tokens(text);
        kind = GenericBoundsKind.Point;
        extents = Vector3.Zero;
        if (tokens.Length == 0 || tokens[0].Equals("pnt", StringComparison.OrdinalIgnoreCase))
        {
            return tokens.Length <= 1;
        }

        if (tokens[0].Equals("box", StringComparison.OrdinalIgnoreCase)
            && tokens.Length == 4
            && TryFloat(tokens[1], 0f, out var x)
            && TryFloat(tokens[2], 0f, out var y)
            && TryFloat(tokens[3], 0f, out var z))
        {
            kind = GenericBoundsKind.Box;
            extents = new Vector3(x, y, z);
            return true;
        }

        if ((tokens[0].Equals("cube", StringComparison.OrdinalIgnoreCase)
             || tokens[0].Equals("sph", StringComparison.OrdinalIgnoreCase))
            && tokens.Length == 2
            && TryFloat(tokens[1], 0f, out var radius))
        {
            kind = tokens[0].Equals("cube", StringComparison.OrdinalIgnoreCase)
                ? GenericBoundsKind.Cube
                : GenericBoundsKind.Sphere;
            extents = new Vector3(radius);
            return true;
        }

        if (tokens[0].Equals("cyl", StringComparison.OrdinalIgnoreCase)
            && tokens.Length == 3
            && TryFloat(tokens[1], 0f, out var cylinderRadius)
            && TryFloat(tokens[2], 0f, out var cylinderLength))
        {
            kind = GenericBoundsKind.Cylinder;
            extents = new Vector3(cylinderRadius, cylinderLength * 0.5f, cylinderRadius);
            return true;
        }

        return false;
    }

    private static bool TryFloatList(string? text, out ImmutableArray<float> values)
    {
        var result = ImmutableArray.CreateBuilder<float>();
        foreach (var token in Tokens(text))
        {
            if (!TryFloat(token, 0f, out var value))
            {
                values = [];
                return false;
            }

            result.Add(value);
        }

        values = result.ToImmutable();
        return true;
    }

    private static bool TryVector(string? text, Vector3 fallback, out Vector3 vector)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            vector = fallback;
            return true;
        }

        var tokens = Tokens(text);
        if (tokens.Length == 3
            && TryFloat(tokens[0], 0f, out var x)
            && TryFloat(tokens[1], 0f, out var y)
            && TryFloat(tokens[2], 0f, out var z))
        {
            vector = new Vector3(x, y, z);
            return true;
        }

        vector = default;
        return false;
    }

    private static bool TryFloat(string? text, float fallback, out float value)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            value = fallback;
            return true;
        }

        return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool ParseBool(string? text) =>
        bool.TryParse(text, out var result) && result;

    private static string[] Tokens(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static FeatureExtractionIssue Issue(string part, ShadowMeta meta, string reason) =>
        new(part, meta.SourceFile, meta.LineNumber, reason);

    private LoadedShadow? LoadShadow(string canonicalName)
    {
        if (_shadowCache.TryGetValue(canonicalName, out var cached))
        {
            return cached;
        }

        foreach (var candidate in ShadowCandidates(canonicalName))
        {
            if (_shadowSource.TryRead(candidate, out var file))
            {
                var parsed = LDraw.Parsing.LDrawParser.Parse(file.Text, canonicalName, file.OriginPath);
                var loaded = new LoadedShadow(candidate, ShadowMetaParser.Extract(parsed.Root, candidate));
                _shadowCache[canonicalName] = loaded;
                return loaded;
            }
        }

        _shadowCache[canonicalName] = null;
        return null;
    }

    private LoadedShadow? LoadIncludedShadow(string containingPath, string reference)
    {
        var canonical = LDrawName.Canonicalize(reference);
        var directory = containingPath.Contains('/')
            ? containingPath[..containingPath.LastIndexOf('/')]
            : string.Empty;

        var candidates = new List<string>();
        if (directory.Length > 0)
        {
            candidates.Add($"{directory}/{canonical}");
        }

        candidates.AddRange(ShadowCandidates(canonical));

        foreach (var candidate in candidates.Distinct(StringComparer.Ordinal))
        {
            if (_shadowSource.TryRead(candidate, out var file))
            {
                var parsed = LDraw.Parsing.LDrawParser.Parse(file.Text, canonical, file.OriginPath);
                return new LoadedShadow(candidate, ShadowMetaParser.Extract(parsed.Root, candidate));
            }
        }

        return null;
    }

    private static IEnumerable<string> ShadowCandidates(string canonicalName)
    {
        var canonical = LDrawName.Canonicalize(canonicalName);
        if (canonical.StartsWith("parts/", StringComparison.Ordinal)
            || canonical.StartsWith("p/", StringComparison.Ordinal))
        {
            yield return canonical;
            yield break;
        }

        yield return $"parts/{canonical}";
        yield return $"p/{canonical}";
        yield return canonical;
    }

    private static bool IsAllowedLocalReference(string reference)
    {
        var normalized = reference.Replace('\\', '/');
        return normalized.Length > 0
            && !Path.IsPathRooted(normalized)
            && !normalized.Contains(':')
            && normalized.Split('/').All(segment => segment is not ".." and not "." and not "");
    }
}

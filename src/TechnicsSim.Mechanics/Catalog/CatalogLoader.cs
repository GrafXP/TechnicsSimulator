using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TechnicsSim.Mechanics.Catalog;

/// <summary>The on-disk shape of <c>data/parts-mechanics.json</c>.</summary>
public sealed class CatalogDocument
{
    public int SchemaVersion { get; set; } = 1;

    public string? Description { get; set; }

    public List<CatalogEntry> Parts { get; set; } = [];
}

public sealed class CatalogEntry
{
    public string Part { get; set; } = string.Empty;

    public MechanicalComponentType Type { get; set; }

    public MechanicalSupport Support { get; set; }

    public int? Teeth { get; set; }

    public float? PitchRadiusLdu { get; set; }

    public float? ToothFaceWidthLdu { get; set; }

    public int? WormStarts { get; set; }

    public WormHandedness? WormHandedness { get; set; }

    public string? MotorInputLabel { get; set; }

    public string? MotorOutputFeatureId { get; set; }

    public float[]? Axis { get; set; }

    public List<MechanicalComponentType>? MeshesWith { get; set; }

    /// <summary>Where the entry's numbers came from. Required, so no value is unattributable.</summary>
    public string Source { get; set; } = string.Empty;

    public string? Note { get; set; }

    public string? UnsupportedReason { get; set; }
}

/// <summary>
/// Reads and validates the reviewed part-semantics table.
///
/// Validation is strict and total: every problem in the file is reported at once rather than
/// throwing on the first, because the usual way to edit this file is to add several parts and
/// then find out what is wrong with all of them.
/// </summary>
public static class CatalogLoader
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static MechanicsCatalog LoadFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Mechanics catalog not found: {path}", path);
        }

        return Parse(File.ReadAllText(path), path);
    }

    public static MechanicsCatalog Parse(string json, string? sourceDescription = null)
    {
        CatalogDocument document;
        try
        {
            document = JsonSerializer.Deserialize<CatalogDocument>(json, JsonOptions)
                ?? throw new CatalogValidationException([new CatalogIssue("(file)", "the document was null")]);
        }
        catch (JsonException exception)
        {
            throw new CatalogValidationException([new CatalogIssue("(file)", exception.Message)]);
        }

        if (document.SchemaVersion != 1)
        {
            throw new CatalogValidationException(
                [new CatalogIssue("(file)", $"unsupported schemaVersion {document.SchemaVersion}; this build reads version 1")]);
        }

        var issues = new List<CatalogIssue>();
        var builder = ImmutableDictionary.CreateBuilder<string, PartMechanics>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in document.Parts)
        {
            var part = (entry.Part ?? string.Empty).Trim();
            if (part.Length == 0)
            {
                issues.Add(new CatalogIssue("(unnamed)", "part is required"));
                continue;
            }

            if (builder.ContainsKey(part))
            {
                issues.Add(new CatalogIssue(part, "duplicate entry"));
                continue;
            }

            var before = issues.Count;
            var mechanics = Convert(part, entry, issues);
            if (issues.Count == before)
            {
                builder[part] = mechanics;
            }
        }

        if (issues.Count > 0)
        {
            throw new CatalogValidationException(issues);
        }

        return new MechanicsCatalog(builder.ToImmutable(), sourceDescription ?? document.Description);
    }

    public static string ToJson(MechanicsCatalog catalog)
    {
        var document = new CatalogDocument
        {
            SchemaVersion = 1,
            Description = catalog.SourceDescription,
            Parts = catalog.Parts.Values
                .OrderBy(part => part.Part, StringComparer.OrdinalIgnoreCase)
                .Select(ToEntry)
                .ToList(),
        };

        return JsonSerializer.Serialize(document, JsonOptions);
    }

    private static PartMechanics Convert(string part, CatalogEntry entry, List<CatalogIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(entry.Source))
        {
            issues.Add(new CatalogIssue(part, "source is required so every value is attributable"));
        }

        var gear = BuildGear(part, entry, issues);
        var worm = BuildWorm(part, entry, issues);

        if (entry.Type == MechanicalComponentType.WormGear && worm is null)
        {
            issues.Add(new CatalogIssue(part, "a WormGear needs wormStarts"));
        }

        if (entry.Type == MechanicalComponentType.Motor && string.IsNullOrWhiteSpace(entry.MotorInputLabel))
        {
            issues.Add(new CatalogIssue(part, "a Motor needs motorInputLabel"));
        }

        if (RequiresTeeth(entry.Type) && gear is null)
        {
            issues.Add(new CatalogIssue(part, $"a {entry.Type} needs teeth"));
        }

        if (entry.Support == MechanicalSupport.UnsupportedBoundary
            && string.IsNullOrWhiteSpace(entry.UnsupportedReason))
        {
            issues.Add(new CatalogIssue(
                part, "an UnsupportedBoundary needs unsupportedReason so the UI can explain the stop"));
        }

        if (entry.Support == MechanicalSupport.Solved && !string.IsNullOrWhiteSpace(entry.UnsupportedReason))
        {
            issues.Add(new CatalogIssue(part, "unsupportedReason is set on a Solved part"));
        }

        var axis = BuildAxis(part, entry, issues);

        return new PartMechanics(
            Part: part,
            Type: entry.Type,
            Support: entry.Support,
            Source: entry.Source ?? string.Empty,
            Gear: gear,
            Worm: worm,
            Motor: string.IsNullOrWhiteSpace(entry.MotorInputLabel)
                ? null
                : new MotorOutput(entry.MotorInputLabel!, entry.MotorOutputFeatureId),
            Axis: axis,
            MeshesWith: entry.MeshesWith is null ? [] : [.. entry.MeshesWith],
            Note: entry.Note,
            UnsupportedReason: entry.UnsupportedReason);
    }

    private static GearGeometry? BuildGear(string part, CatalogEntry entry, List<CatalogIssue> issues)
    {
        if (entry.Teeth is null)
        {
            // A worm carries a measured pitch radius with no tooth count, which is the one case
            // where the two are legitimately independent.
            if (entry.PitchRadiusLdu is not null && entry.Type != MechanicalComponentType.WormGear)
            {
                issues.Add(new CatalogIssue(part, "pitchRadiusLdu is set without teeth"));
            }

            return null;
        }

        if (entry.Teeth <= 0)
        {
            issues.Add(new CatalogIssue(part, $"teeth must be positive, got {entry.Teeth}"));
            return null;
        }

        if (entry.PitchRadiusLdu is <= 0)
        {
            issues.Add(new CatalogIssue(part, $"pitchRadiusLdu must be positive, got {entry.PitchRadiusLdu}"));
        }

        if (entry.ToothFaceWidthLdu is <= 0)
        {
            issues.Add(new CatalogIssue(part, $"toothFaceWidthLdu must be positive, got {entry.ToothFaceWidthLdu}"));
        }

        return new GearGeometry(entry.Teeth.Value, entry.PitchRadiusLdu, entry.ToothFaceWidthLdu);
    }

    private static WormGeometry? BuildWorm(string part, CatalogEntry entry, List<CatalogIssue> issues)
    {
        if (entry.WormStarts is null)
        {
            return null;
        }

        if (entry.WormStarts <= 0)
        {
            issues.Add(new CatalogIssue(part, $"wormStarts must be positive, got {entry.WormStarts}"));
            return null;
        }

        return new WormGeometry(
            entry.WormStarts.Value,
            entry.WormHandedness ?? Catalog.WormHandedness.Right,
            entry.PitchRadiusLdu);
    }

    private static Vector3? BuildAxis(string part, CatalogEntry entry, List<CatalogIssue> issues)
    {
        if (entry.Axis is null)
        {
            return null;
        }

        if (entry.Axis.Length != 3)
        {
            issues.Add(new CatalogIssue(part, $"axis needs three components, got {entry.Axis.Length}"));
            return null;
        }

        var axis = new Vector3(entry.Axis[0], entry.Axis[1], entry.Axis[2]);
        if (axis.LengthSquared() < 1e-6f)
        {
            issues.Add(new CatalogIssue(part, "axis is degenerate"));
            return null;
        }

        return Vector3.Normalize(axis);
    }

    private static bool RequiresTeeth(MechanicalComponentType type) => type switch
    {
        MechanicalComponentType.SpurGear => true,
        MechanicalComponentType.BevelGear => true,
        MechanicalComponentType.DoubleBevelGear => true,
        MechanicalComponentType.CrownGear => true,
        MechanicalComponentType.KnobGear => true,
        MechanicalComponentType.ClutchGear => true,
        MechanicalComponentType.Turntable => true,
        _ => false,
    };

    private static CatalogEntry ToEntry(PartMechanics part) => new()
    {
        Part = part.Part,
        Type = part.Type,
        Support = part.Support,
        Teeth = part.Gear?.Teeth,
        PitchRadiusLdu = part.Gear?.PitchRadiusLdu ?? part.Worm?.PitchRadiusLdu,
        ToothFaceWidthLdu = part.Gear?.ToothFaceWidthLdu,
        WormStarts = part.Worm?.Starts,
        WormHandedness = part.Worm?.Handedness,
        MotorInputLabel = part.Motor?.DefaultInputLabel,
        MotorOutputFeatureId = part.Motor?.OutputFeatureId,
        Axis = part.Axis is { } axis ? [axis.X, axis.Y, axis.Z] : null,
        MeshesWith = part.MeshPartners.IsEmpty ? null : [.. part.MeshPartners],
        Source = part.Source,
        Note = part.Note,
        UnsupportedReason = part.UnsupportedReason,
    };
}

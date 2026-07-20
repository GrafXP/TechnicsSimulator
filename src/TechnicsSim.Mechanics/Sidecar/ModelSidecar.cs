using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TechnicsSim.Mechanics.Sidecar;

/// <summary>How a reviewer resolved something automatic inference could not decide.</summary>
public enum ReviewDecision
{
    /// <summary>Treat as real even though inference did not accept it.</summary>
    Accept,

    /// <summary>Treat as absent even though inference proposed it.</summary>
    Reject,
}

/// <summary>A clutch's reviewed engagement state.</summary>
public enum ClutchState
{
    /// <summary>Torque passes; the gear and its axle share one angular velocity.</summary>
    Locked,

    /// <summary>The gear spins independently of its axle.</summary>
    Free,
}

/// <summary>A reviewed decision about one proposed gear mesh.</summary>
public sealed record MeshOverride(
    string GearA,
    string GearB,
    ReviewDecision Decision,
    string Reason);

/// <summary>A reviewed decision about one proposed connection.</summary>
public sealed record MateOverride(
    string FeatureA,
    string FeatureB,
    ReviewDecision Decision,
    string Reason);

/// <summary>Instances a reviewer says belong on one shaft, or must not.</summary>
public sealed record ShaftOverride(
    ImmutableArray<string> InstanceIds,
    ReviewDecision Decision,
    string Reason);

/// <summary>A clutch instance whose engagement a reviewer has decided.</summary>
public sealed record ClutchOverride(
    string InstanceId,
    ClutchState State,
    string Reason);

/// <summary>A named input the UI can drive.</summary>
public sealed record DriverDefinition(
    string InstanceId,
    string Label,
    string Reason);

/// <summary>A mechanism a reviewer wants explicitly labelled as out of scope.</summary>
public sealed record UnsupportedAnnotation(
    string InstanceId,
    string Reason);

/// <summary>
/// Model-specific corrections, reviewed by a human and committed alongside the model.
///
/// The point is determinism. Automatic inference stays useful, but a shipped demonstration
/// should not depend on a tolerance happening to fall the right way, so anything ambiguous is
/// resolved here in a file that can be read and argued with.
///
/// Every override carries a <c>reason</c> and every entry a fingerprint of the part it refers
/// to. Instance ids encode positions in the model tree, so editing the model silently
/// repoints them; the fingerprint makes that detectable rather than mysterious.
/// </summary>
public sealed record ModelSidecar(
    int SchemaVersion,
    string Model,
    ImmutableArray<string> GroundInstanceIds,
    ImmutableArray<MateOverride> Mates,
    ImmutableArray<ShaftOverride> Shafts,
    ImmutableArray<MeshOverride> Meshes,
    ImmutableArray<ClutchOverride> Clutches,
    ImmutableArray<DriverDefinition> Drivers,
    ImmutableArray<UnsupportedAnnotation> Unsupported,
    ImmutableDictionary<string, string> InstanceFingerprints,
    string? Note = null)
{
    public const int CurrentSchemaVersion = 1;

    public static ModelSidecar Empty(string model) => new(
        CurrentSchemaVersion,
        model,
        [],
        [],
        [],
        [],
        [],
        [],
        [],
        ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal));

    public bool IsEmpty =>
        GroundInstanceIds.IsEmpty
        && Mates.IsEmpty
        && Shafts.IsEmpty
        && Meshes.IsEmpty
        && Clutches.IsEmpty
        && Drivers.IsEmpty
        && Unsupported.IsEmpty;

    public ClutchState? ClutchStateFor(string instanceId) =>
        Clutches.FirstOrDefault(clutch => clutch.InstanceId == instanceId)?.State;

    public ReviewDecision? MeshDecisionFor(string gearA, string gearB) =>
        Meshes.FirstOrDefault(mesh =>
            (mesh.GearA == gearA && mesh.GearB == gearB)
            || (mesh.GearA == gearB && mesh.GearB == gearA))?.Decision;
}

/// <summary>A sidecar entry that no longer matches the model it annotates.</summary>
public sealed record StaleSidecarEntry(string InstanceId, string Expected, string Actual);

public static class ModelSidecarIo
{
    public const string Suffix = ".mechanics.json";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The sidecar path for a model, for example Models/8275-1.mechanics.json.</summary>
    public static string PathFor(string modelPath) => Path.Combine(
        Path.GetDirectoryName(modelPath) ?? string.Empty,
        Path.GetFileNameWithoutExtension(modelPath) + Suffix);

    /// <summary>Loads a model's sidecar, or an empty one when none is committed.</summary>
    public static ModelSidecar LoadFor(string modelPath)
    {
        var path = PathFor(modelPath);
        return File.Exists(path)
            ? Parse(File.ReadAllText(path), Path.GetFileName(modelPath))
            : ModelSidecar.Empty(Path.GetFileName(modelPath));
    }

    public static ModelSidecar Parse(string json, string modelFileName)
    {
        var sidecar = JsonSerializer.Deserialize<ModelSidecar>(json, JsonOptions)
            ?? throw new InvalidDataException($"The sidecar for {modelFileName} was empty.");

        if (sidecar.SchemaVersion != ModelSidecar.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"The sidecar for {modelFileName} declares schemaVersion {sidecar.SchemaVersion}; "
                + $"this build reads version {ModelSidecar.CurrentSchemaVersion}.");
        }

        return Normalize(sidecar);
    }

    public static void Save(string modelPath, ModelSidecar sidecar) =>
        File.WriteAllText(PathFor(modelPath), ToJson(sidecar));

    public static string ToJson(ModelSidecar sidecar) =>
        JsonSerializer.Serialize(Normalize(sidecar), JsonOptions);

    /// <summary>
    /// Reports entries whose recorded fingerprint disagrees with the model.
    ///
    /// A stale override is worse than a missing one, because it looks reviewed. Rather than
    /// dropping or applying it, both are surfaced so the reviewer decides.
    /// </summary>
    public static ImmutableArray<StaleSidecarEntry> FindStaleEntries(
        ModelSidecar sidecar,
        IReadOnlyDictionary<string, string> currentFingerprints)
    {
        var stale = ImmutableArray.CreateBuilder<StaleSidecarEntry>();

        foreach (var (instanceId, expected) in sidecar.InstanceFingerprints)
        {
            var actual = currentFingerprints.GetValueOrDefault(instanceId);
            if (actual is null)
            {
                stale.Add(new StaleSidecarEntry(instanceId, expected, "(no such instance)"));
            }
            else if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                stale.Add(new StaleSidecarEntry(instanceId, expected, actual));
            }
        }

        return [.. stale.OrderBy(entry => entry.InstanceId, StringComparer.Ordinal)];
    }

    /// <summary>Sorts every collection so a re-exported sidecar diffs cleanly against the last one.</summary>
    private static ModelSidecar Normalize(ModelSidecar sidecar) => sidecar with
    {
        GroundInstanceIds = [.. sidecar.GroundInstanceIds.OrderBy(id => id, StringComparer.Ordinal)],
        Mates = [.. sidecar.Mates.OrderBy(mate => mate.FeatureA, StringComparer.Ordinal).ThenBy(mate => mate.FeatureB, StringComparer.Ordinal)],
        Shafts = [.. sidecar.Shafts.OrderBy(shaft => shaft.InstanceIds.FirstOrDefault() ?? string.Empty, StringComparer.Ordinal)],
        Meshes = [.. sidecar.Meshes.OrderBy(mesh => mesh.GearA, StringComparer.Ordinal).ThenBy(mesh => mesh.GearB, StringComparer.Ordinal)],
        Clutches = [.. sidecar.Clutches.OrderBy(clutch => clutch.InstanceId, StringComparer.Ordinal)],
        Drivers = [.. sidecar.Drivers.OrderBy(driver => driver.InstanceId, StringComparer.Ordinal)],
        Unsupported = [.. sidecar.Unsupported.OrderBy(entry => entry.InstanceId, StringComparer.Ordinal)],
        InstanceFingerprints = sidecar.InstanceFingerprints.IsEmpty
            ? ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal)
            : sidecar.InstanceFingerprints,
    };
}

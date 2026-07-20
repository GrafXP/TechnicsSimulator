using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using TechnicsSim.LDraw.Expansion;
using TechnicsSim.Mechanics.Catalog;
using TechnicsSim.Mechanics.Shafts;

namespace TechnicsSim.Mechanics.Sidecar;

/// <summary>What a sidecar changed, so the CLI and UI can show the review rather than hide it.</summary>
public sealed record SidecarEffect(
    ImmutableArray<string> AcceptedMeshes,
    ImmutableArray<string> RejectedMeshes,
    ImmutableArray<string> LockedClutches,
    ImmutableArray<string> FreedClutches,
    ImmutableArray<StaleSidecarEntry> StaleEntries)
{
    public static SidecarEffect None { get; } = new([], [], [], [], []);

    public bool ChangedAnything =>
        !AcceptedMeshes.IsEmpty || !RejectedMeshes.IsEmpty
        || !LockedClutches.IsEmpty || !FreedClutches.IsEmpty;
}

/// <summary>
/// Applies reviewed corrections to an automatically built graph.
///
/// Order matters. Clutches are resolved before meshes are proposed, because a locked clutch
/// stops being a boundary and becomes an ordinary gear that can then mesh; doing it the other
/// way round would leave a confirmed clutch with no partners.
/// </summary>
public static class SidecarApplication
{
    /// <summary>
    /// Builds the graph with a sidecar applied.
    ///
    /// A locked clutch is promoted to a solved gear of its catalogued type before the graph is
    /// built, so it participates in mesh detection exactly as the equivalent plain gear would.
    /// </summary>
    public static (ShaftGraph Graph, SidecarEffect Effect) Build(
        Mating.ConnectionAnalysis analysis,
        ModelExpansion expansion,
        MechanicsCatalog catalog,
        ModelSidecar sidecar,
        ShaftGraphOptions? options = null)
    {
        var stale = ModelSidecarIo.FindStaleEntries(sidecar, Fingerprints(expansion));

        var locked = sidecar.Clutches
            .Where(clutch => clutch.State == ClutchState.Locked)
            .Select(clutch => clutch.InstanceId)
            .ToImmutableArray();

        var freed = sidecar.Clutches
            .Where(clutch => clutch.State == ClutchState.Free)
            .Select(clutch => clutch.InstanceId)
            .ToImmutableArray();

        var effectiveCatalog = locked.IsEmpty
            ? catalog
            : PromoteLockedClutches(catalog, expansion, locked);

        var graph = ShaftGraphBuilder.Build(analysis, expansion, effectiveCatalog, options);
        var (meshes, accepted, rejected) = ApplyMeshOverrides(graph, sidecar);

        return (
            graph with { Meshes = meshes },
            new SidecarEffect(accepted, rejected, locked, freed, stale));
    }

    /// <summary>
    /// A fingerprint that changes when the instance stops being the same physical thing.
    ///
    /// Part name plus rounded world position: enough that moving or replacing a part invalidates
    /// its overrides, without being so brittle that a harmless reordering elsewhere does.
    /// </summary>
    public static ImmutableDictionary<string, string> Fingerprints(ModelExpansion expansion)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);

        foreach (var instance in expansion.Instances)
        {
            builder[instance.InstanceId] = Fingerprint(instance);
        }

        return builder.ToImmutable();
    }

    public static string Fingerprint(LogicalPartInstance instance)
    {
        var position = Vector3.Transform(Vector3.Zero, instance.Transform);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{instance.CanonicalPartName}@{position.X:F1},{position.Y:F1},{position.Z:F1}");
    }

    /// <summary>
    /// Rewrites confirmed-locked clutch gears as ordinary gears of their catalogued kind.
    ///
    /// The clutch's tooth count and pitch are already known; only the coupling to the axle was
    /// in doubt, and the reviewer has now settled it.
    /// </summary>
    private static MechanicsCatalog PromoteLockedClutches(
        MechanicsCatalog catalog, ModelExpansion expansion, ImmutableArray<string> lockedInstanceIds)
    {
        var lockedParts = expansion.Instances
            .Where(instance => lockedInstanceIds.Contains(instance.InstanceId, StringComparer.Ordinal))
            .Select(instance => instance.CanonicalPartName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var parts = catalog.Parts;

        foreach (var partName in lockedParts)
        {
            if (parts.TryGetValue(partName, out var mechanics)
                && mechanics.Type == MechanicalComponentType.ClutchGear)
            {
                parts = parts.SetItem(partName, mechanics with
                {
                    Type = MechanicalComponentType.SpurGear,
                    Support = MechanicalSupport.Solved,
                    UnsupportedReason = null,
                    Note = $"Confirmed locked by the model sidecar. {mechanics.Note}".TrimEnd(),
                });
            }
        }

        return catalog with { Parts = parts };
    }

    private static (
        ImmutableArray<GearMesh> Meshes,
        ImmutableArray<string> Accepted,
        ImmutableArray<string> Rejected)
        ApplyMeshOverrides(ShaftGraph graph, ModelSidecar sidecar)
    {
        if (sidecar.Meshes.IsEmpty)
        {
            return (graph.Meshes, [], []);
        }

        var rejected = ImmutableArray.CreateBuilder<string>();
        var kept = ImmutableArray.CreateBuilder<GearMesh>();

        foreach (var mesh in graph.Meshes)
        {
            if (sidecar.MeshDecisionFor(mesh.GearA, mesh.GearB) == ReviewDecision.Reject)
            {
                rejected.Add($"{mesh.GearA} <-> {mesh.GearB}");
                continue;
            }

            kept.Add(mesh);
        }

        // An accepted override that inference already found is not an error; it just records
        // that a human agreed. Only the ones inference missed are worth reporting as promotions.
        var present = graph.Meshes
            .Select(mesh => Key(mesh.GearA, mesh.GearB))
            .ToHashSet(StringComparer.Ordinal);

        var accepted = sidecar.Meshes
            .Where(mesh => mesh.Decision == ReviewDecision.Accept)
            .Where(mesh => !present.Contains(Key(mesh.GearA, mesh.GearB)))
            .Select(mesh => $"{mesh.GearA} <-> {mesh.GearB}")
            .ToImmutableArray();

        return (kept.ToImmutable(), accepted, rejected.ToImmutable());

        static string Key(string a, string b) =>
            string.CompareOrdinal(a, b) <= 0 ? $"{a}|{b}" : $"{b}|{a}";
    }
}

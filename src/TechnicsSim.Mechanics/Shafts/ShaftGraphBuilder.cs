using System.Collections.Immutable;
using System.Numerics;
using TechnicsSim.LDraw.Expansion;
using TechnicsSim.Mechanics.Catalog;
using TechnicsSim.Mechanics.Features;
using TechnicsSim.Mechanics.Mating;

namespace TechnicsSim.Mechanics.Shafts;

/// <summary>
/// Turns classified connections plus catalog semantics into shaft assemblies and gear meshes.
///
/// The construction order is deliberate. Shafts come first and are built only from keyed
/// connections, so the rotary graph starts from the one relation whose mechanics are not in
/// doubt. Gears are then mounted onto those shafts, and only then are meshes proposed. Trying
/// to infer a rigid body decomposition first would need pin grouping, which is exactly the
/// ambiguous part.
/// </summary>
public static class ShaftGraphBuilder
{
    public static ShaftGraph Build(
        ConnectionAnalysis analysis,
        ModelExpansion expansion,
        MechanicsCatalog catalog,
        ShaftGraphOptions? options = null)
    {
        var opts = options ?? new ShaftGraphOptions();
        var instancesById = expansion.Instances.ToDictionary(
            instance => instance.InstanceId, StringComparer.Ordinal);

        var shafts = BuildShafts(analysis, opts);
        var shaftByInstance = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var shaft in shafts)
        {
            foreach (var instanceId in shaft.InstanceIds)
            {
                shaftByInstance[instanceId] = shaft.ShaftId;
            }
        }

        var (gears, unsupported, uncatalogued, extraShafts, drivers) =
            MountComponents(analysis, expansion, instancesById, catalog, shafts, shaftByInstance, opts);

        var allShafts = shafts.AddRange(extraShafts);
        var (meshes, ambiguous) = DetectMeshes(gears, allShafts, opts);

        return new ShaftGraph(
            allShafts,
            gears,
            meshes,
            ambiguous,
            unsupported,
            uncatalogued,
            drivers);
    }

    /// <summary>
    /// Unions instances joined by keyed connections.
    ///
    /// Only <see cref="ConnectionKind.KeyedCoaxial"/> joins. A bearing is recorded against the
    /// shaft it supports but never merged into it, because a bearing transfers no torque, and a
    /// geometric mate is left out entirely.
    /// </summary>
    private static ImmutableArray<ShaftAssembly> BuildShafts(
        ConnectionAnalysis analysis, ShaftGraphOptions options)
    {
        var union = new UnionFind();
        foreach (var connection in analysis.Connections.Where(c => c.Kind == ConnectionKind.KeyedCoaxial))
        {
            union.Union(connection.InstanceA, connection.InstanceB);
        }

        var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var instanceId in union.Members)
        {
            var root = union.Find(instanceId);
            if (!groups.TryGetValue(root, out var members))
            {
                groups[root] = members = [];
            }

            members.Add(instanceId);
        }

        // Features that carried the keyed relations, so a shaft's axis is measured from the
        // geometry that justified it rather than from an arbitrary member part.
        var featuresByGroup = new Dictionary<string, List<PlacedFeature>>(StringComparer.Ordinal);
        foreach (var connection in analysis.Connections.Where(c => c.Kind == ConnectionKind.KeyedCoaxial))
        {
            var root = union.Find(connection.InstanceA);
            if (!featuresByGroup.TryGetValue(root, out var features))
            {
                featuresByGroup[root] = features = [];
            }

            if (analysis.FindFeature(connection.FeatureA) is { } a)
            {
                features.Add(a);
            }

            if (analysis.FindFeature(connection.FeatureB) is { } b)
            {
                features.Add(b);
            }
        }

        var bearingsByGroup = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var connection in analysis.Connections.Where(c => c.Kind == ConnectionKind.RevoluteBearing))
        {
            AddBearing(connection.InstanceA, connection.InstanceB);
            AddBearing(connection.InstanceB, connection.InstanceA);

            void AddBearing(string shaftSide, string supportSide)
            {
                if (!union.Contains(shaftSide))
                {
                    return;
                }

                var root = union.Find(shaftSide);
                if (!bearingsByGroup.TryGetValue(root, out var set))
                {
                    bearingsByGroup[root] = set = new HashSet<string>(StringComparer.Ordinal);
                }

                set.Add(supportSide);
            }
        }

        var result = ImmutableArray.CreateBuilder<ShaftAssembly>(groups.Count);
        var ordered = groups.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToList();

        for (var i = 0; i < ordered.Count; i++)
        {
            var (root, members) = (ordered[i].Key, ordered[i].Value);
            var features = featuresByGroup.GetValueOrDefault(root, []);
            var (origin, axis) = AverageAxis(features);

            members.Sort(StringComparer.Ordinal);
            var bearings = bearingsByGroup.GetValueOrDefault(root, [])
                .Where(id => !members.Contains(id, StringComparer.Ordinal))
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToImmutableArray();

            result.Add(new ShaftAssembly(
                ShaftId: $"shaft-{i:D4}",
                InstanceIds: [.. members],
                Origin: origin,
                Axis: axis,
                MemberFeatureKeys: [.. features.Select(feature => feature.Key).Distinct(StringComparer.Ordinal).OrderBy(key => key, StringComparer.Ordinal)],
                BearingInstanceIds: bearings));
        }

        _ = options;
        return result.ToImmutable();
    }

    /// <summary>
    /// Averages feature axes into one shaft axis, folding antiparallel directions together
    /// first so two axle halves pointing at each other do not cancel to nothing.
    /// </summary>
    private static (Vector3 Origin, Vector3 Axis) AverageAxis(IReadOnlyList<PlacedFeature> features)
    {
        if (features.Count == 0)
        {
            return (Vector3.Zero, Vector3.UnitY);
        }

        var reference = FeatureGeometry.Axis(features[0]).Direction;
        var accumulated = Vector3.Zero;
        var origin = Vector3.Zero;

        foreach (var feature in features)
        {
            var axis = FeatureGeometry.Axis(feature);
            var direction = Vector3.Dot(axis.Direction, reference) < 0 ? -axis.Direction : axis.Direction;
            accumulated += direction;
            origin += axis.Centre;
        }

        var normalized = accumulated.LengthSquared() > 1e-8f
            ? Vector3.Normalize(accumulated)
            : reference;

        return (origin / features.Count, normalized);
    }

    private static (
        ImmutableArray<MountedGear> Gears,
        ImmutableArray<UnsupportedComponent> Unsupported,
        ImmutableArray<UncataloguedComponent> Uncatalogued,
        ImmutableArray<ShaftAssembly> ExtraShafts,
        ImmutableArray<MountedDriver> Drivers)
        MountComponents(
            ConnectionAnalysis analysis,
            ModelExpansion expansion,
            IReadOnlyDictionary<string, LogicalPartInstance> instancesById,
            MechanicsCatalog catalog,
            ImmutableArray<ShaftAssembly> shafts,
            Dictionary<string, string> shaftByInstance,
            ShaftGraphOptions options)
    {
        var shaftsById = shafts.ToDictionary(shaft => shaft.ShaftId, StringComparer.Ordinal);
        var gears = ImmutableArray.CreateBuilder<MountedGear>();
        var unsupported = ImmutableArray.CreateBuilder<UnsupportedComponent>();
        var uncatalogued = new Dictionary<string, UncataloguedComponent>(StringComparer.OrdinalIgnoreCase);
        var extraShafts = ImmutableArray.CreateBuilder<ShaftAssembly>();
        var drivers = ImmutableArray.CreateBuilder<MountedDriver>();
        var nextExtra = 0;

        var featuresByInstance = analysis.Features
            .GroupBy(feature => feature.InstanceId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

        foreach (var instance in expansion.Instances)
        {
            var mechanics = catalog.Find(instance.CanonicalPartName);
            if (mechanics is null)
            {
                continue;
            }

            var shaftId = shaftByInstance.GetValueOrDefault(instance.InstanceId);

            if (mechanics.Support == MechanicalSupport.UnsupportedBoundary)
            {
                unsupported.Add(new UnsupportedComponent(
                    instance.InstanceId,
                    instance.CanonicalPartName,
                    mechanics.Type,
                    mechanics.UnsupportedReason ?? "Marked unsupported by the mechanics catalog.",
                    shaftId));
                continue;
            }

            if (mechanics.Type == MechanicalComponentType.Motor)
            {
                // A motor is neither a gear nor a boundary. Without its own list it would drop
                // out of the graph, taking the solver's entry points with it.
                drivers.Add(new MountedDriver(
                    instance.InstanceId,
                    instance.CanonicalPartName,
                    shaftId,
                    mechanics.Motor?.DefaultInputLabel ?? instance.CanonicalPartName));
                continue;
            }

            if (!mechanics.IsToothed)
            {
                // Keyed riders join a shaft but propose no mesh of their own.
                continue;
            }

            var placement = ResolveAxis(instance, featuresByInstance, mechanics, shaftId, shaftsById);
            if (placement is null)
            {
                uncatalogued.TryAdd(instance.CanonicalPartName, new UncataloguedComponent(
                    instance.InstanceId,
                    instance.CanonicalPartName,
                    "Catalogued as toothed, but no keyed axle feature or explicit axis gave it a rotation axis.",
                    expansion.PartUsage.GetValueOrDefault(instance.CanonicalPartName, 0)));
                continue;
            }

            var (centre, axis, halfWidth, axisSource) = placement.Value;

            if (shaftId is null)
            {
                // A gear with no keyed relation still exists and can still mesh. Giving it a
                // single-member shaft keeps it visible rather than dropping it from the graph.
                shaftId = $"shaft-free-{nextExtra++:D4}";
                extraShafts.Add(new ShaftAssembly(
                    shaftId,
                    [instance.InstanceId],
                    centre,
                    axis,
                    [],
                    []));
                shaftByInstance[instance.InstanceId] = shaftId;
            }

            // A worm's radius is measured rather than derived, so it lives on the worm geometry.
            var pitchRadius = mechanics.Gear?.EffectivePitchRadiusLdu
                ?? mechanics.Worm?.PitchRadiusLdu
                ?? 0f;

            gears.Add(new MountedGear(
                instance.InstanceId,
                instance.CanonicalPartName,
                shaftId,
                mechanics,
                centre,
                axis,
                pitchRadius,
                mechanics.Gear?.ToothFaceWidthLdu is { } width ? width / 2f : halfWidth,
                axisSource));
        }

        _ = instancesById;
        _ = options;

        return (
            gears.ToImmutable(),
            unsupported.ToImmutable(),
            [.. uncatalogued.Values.OrderByDescending(part => part.InstanceCount)],
            extraShafts.ToImmutable(),
            [.. drivers.OrderBy(driver => driver.InstanceId, StringComparer.Ordinal)]);
    }

    /// <summary>
    /// Establishes a gear's rotation axis, centre, and half face width.
    ///
    /// The keyed axle feature is the authority, because gear parts do not share one local axis
    /// convention: 3647 lays its axle hole along Z while others use Y. An explicit catalog axis
    /// is an override for parts whose geometry does not supply one, and the shaft axis is the
    /// last resort.
    /// </summary>
    private static (Vector3 Centre, Vector3 Axis, float HalfWidth, string Source)? ResolveAxis(
        LogicalPartInstance instance,
        IReadOnlyDictionary<string, List<PlacedFeature>> featuresByInstance,
        PartMechanics mechanics,
        string? shaftId,
        IReadOnlyDictionary<string, ShaftAssembly> shaftsById)
    {
        if (featuresByInstance.TryGetValue(instance.InstanceId, out var features))
        {
            var axle = features
                .Select(feature => (Feature: feature, Axis: FeatureGeometry.Axis(feature)))
                .Where(pair => pair.Feature.Feature.Shape is CylinderSnapShape cylinder
                    && cylinder.Sections.Any(section => section.Kind == CylinderSectionKind.Axle))
                .OrderByDescending(pair => pair.Axis.Length)
                .FirstOrDefault();

            if (axle.Feature is not null)
            {
                return (
                    axle.Axis.Centre,
                    axle.Axis.Direction,
                    Math.Max(axle.Axis.Length / 2f, 0.5f),
                    "keyed axle feature");
            }

            // No axle profile: fall back to the longest cylindrical feature, which for a gear
            // with a round bore still runs along the rotation axis.
            var bore = features
                .Select(feature => (Feature: feature, Axis: FeatureGeometry.Axis(feature)))
                .Where(pair => pair.Feature.Feature.Shape is CylinderSnapShape)
                .OrderByDescending(pair => pair.Axis.Length)
                .FirstOrDefault();

            if (bore.Feature is not null && bore.Axis.Length > 0)
            {
                return (bore.Axis.Centre, bore.Axis.Direction, Math.Max(bore.Axis.Length / 2f, 0.5f), "round bore feature");
            }
        }

        if (mechanics.Axis is { } explicitAxis)
        {
            var centre = Vector3.Transform(Vector3.Zero, instance.Transform);
            var axis = Vector3.Normalize(Vector3.TransformNormal(explicitAxis, instance.Transform));
            return (centre, axis, 4f, "catalog axis override");
        }

        if (shaftId is not null && shaftsById.TryGetValue(shaftId, out var shaft))
        {
            return (Vector3.Transform(Vector3.Zero, instance.Transform), shaft.Axis, 4f, "shaft axis");
        }

        return null;
    }

    /// <summary>
    /// Proposes gear meshes between gears on different shafts.
    ///
    /// A pair is only offered when the catalog says the two component types can mesh, the axis
    /// relationship matches that mesh kind, the measured contact distance agrees with the pitch
    /// radii, and the tooth faces overlap. Any gear left with more than one surviving partner is
    /// reported as ambiguous instead of resolved by iteration order.
    /// </summary>
    private static (ImmutableArray<GearMesh> Meshes, ImmutableArray<AmbiguousMesh> Ambiguous) DetectMeshes(
        ImmutableArray<MountedGear> gears, ImmutableArray<ShaftAssembly> shafts, ShaftGraphOptions options)
    {
        _ = shafts;
        var candidates = new List<GearMesh>();

        for (var i = 0; i < gears.Length; i++)
        {
            for (var j = i + 1; j < gears.Length; j++)
            {
                var a = gears[i];
                var b = gears[j];

                if (a.ShaftId == b.ShaftId)
                {
                    // Two gears already locked to one shaft cannot also mesh with each other.
                    continue;
                }

                if (GearContact.TryMesh(a, b, options, out var mesh))
                {
                    candidates.Add(mesh);
                }
            }
        }

        // A gear meshing with several partners is normal in a gearbox, so ambiguity is judged
        // per partner pair rather than per gear: the problem case is two candidates that would
        // drive the same pair of shafts differently.
        var ambiguous = ImmutableArray.CreateBuilder<AmbiguousMesh>();
        var accepted = ImmutableArray.CreateBuilder<GearMesh>();

        foreach (var group in candidates.GroupBy(mesh => ShaftPairKey(mesh)))
        {
            var inGroup = group.ToList();
            if (inGroup.Count == 1)
            {
                accepted.Add(inGroup[0]);
                continue;
            }

            var distinctRatios = inGroup
                .Select(mesh => (mesh.RatioNumerator, mesh.RatioDenominator, mesh.Sign))
                .Distinct()
                .Count();

            if (distinctRatios == 1)
            {
                // Several tooth pairs between the same shafts that all agree. Keep the closest
                // fit and treat the rest as duplicates rather than a conflict.
                accepted.Add(inGroup.OrderBy(mesh => Math.Abs(mesh.CentreResidualLdu)).First());
                continue;
            }

            var best = inGroup.OrderBy(mesh => Math.Abs(mesh.CentreResidualLdu)).ToList();
            ambiguous.Add(new AmbiguousMesh(
                best[0].GearA,
                [.. best.Select(mesh => mesh.GearB)],
                $"{best.Count} candidate meshes between the same shaft pair disagree on ratio; "
                    + "confirm one in the model sidecar."));
        }

        return (
            [.. accepted.OrderBy(mesh => mesh.GearA, StringComparer.Ordinal).ThenBy(mesh => mesh.GearB, StringComparer.Ordinal)],
            ambiguous.ToImmutable());

        static string ShaftPairKey(GearMesh mesh) =>
            string.CompareOrdinal(mesh.ShaftA, mesh.ShaftB) <= 0
                ? $"{mesh.ShaftA}|{mesh.ShaftB}"
                : $"{mesh.ShaftB}|{mesh.ShaftA}";
    }
}

/// <summary>Disjoint-set over instance ids, used to grow shafts from keyed pairs.</summary>
internal sealed class UnionFind
{
    private readonly Dictionary<string, string> _parent = new(StringComparer.Ordinal);

    public IEnumerable<string> Members => _parent.Keys;

    public bool Contains(string id) => _parent.ContainsKey(id);

    public string Find(string id)
    {
        if (!_parent.TryGetValue(id, out var parent))
        {
            _parent[id] = id;
            return id;
        }

        if (parent == id)
        {
            return id;
        }

        var root = Find(parent);
        _parent[id] = root;
        return root;
    }

    public void Union(string a, string b)
    {
        var rootA = Find(a);
        var rootB = Find(b);
        if (rootA == rootB)
        {
            return;
        }

        // Ordinal ordering keeps the chosen root deterministic across runs.
        if (string.CompareOrdinal(rootA, rootB) <= 0)
        {
            _parent[rootB] = rootA;
        }
        else
        {
            _parent[rootA] = rootB;
        }
    }
}

using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using TechnicsSim.Mechanics.Catalog;
using TechnicsSim.Mechanics.Shafts;

namespace TechnicsSim.Mechanics.Shafts;

public sealed record ShaftGraphReportModel(string File, string RootSection);

public sealed record ShaftGraphReportSources(string? OfficialLibraryVersion, string? ShadowCommit);

public sealed record ShaftGraphReportCounts(
    int Shafts,
    int MultiMemberShafts,
    int Gears,
    int Meshes,
    int AmbiguousMeshes,
    int UnsupportedComponents,
    int UncataloguedComponents);

public sealed record ShaftReportEntry(
    string ShaftId,
    int MemberCount,
    ImmutableArray<string> InstanceIds,
    float[] Origin,
    float[] Axis,
    ImmutableArray<string> BearingInstanceIds);

public sealed record GearReportEntry(
    string InstanceId,
    string Part,
    string ShaftId,
    MechanicalComponentType Type,
    int? Teeth,
    int? WormStarts,
    float PitchRadiusLdu,
    float FaceHalfWidthLdu,
    string AxisSource,
    float[] Centre,
    float[] Axis);

public sealed record MeshReportEntry(
    string GearA,
    string GearB,
    string PartA,
    string PartB,
    string ShaftA,
    string ShaftB,
    GearMeshKind Kind,
    int? TeethA,
    int? TeethB,
    int RatioNumerator,
    int RatioDenominator,
    int Sign,
    string Ratio,
    float CentreDistanceLdu,
    float? ExpectedCentreDistanceLdu,
    float? CentreResidualLdu,
    float FaceOverlapLdu,
    float AxisAngleDegrees,
    ConfidenceLevel Confidence,
    string Rule);

public sealed record UnsupportedReportEntry(
    string Part,
    MechanicalComponentType Type,
    int InstanceCount,
    string Reason,
    ImmutableArray<string> InstanceIds);

public sealed record ShaftGraphReport(
    int SchemaVersion,
    ShaftGraphReportModel Model,
    ShaftGraphReportSources Sources,
    ShaftGraphReportCounts Counts,
    ImmutableArray<ShaftReportEntry> Shafts,
    ImmutableArray<GearReportEntry> Gears,
    ImmutableArray<MeshReportEntry> Meshes,
    ImmutableArray<AmbiguousMesh> AmbiguousMeshes,
    ImmutableArray<UnsupportedReportEntry> Unsupported,
    ImmutableArray<UncataloguedComponent> Uncatalogued)
{
    /// <summary>Drops host-specific provenance so golden comparisons survive a different checkout.</summary>
    public ShaftGraphReport WithoutSourceProvenance() =>
        this with { Sources = new ShaftGraphReportSources(null, null) };
}

public static class ShaftGraphReportBuilder
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static ShaftGraphReport Build(
        ShaftGraph graph,
        string modelFile,
        string rootSection,
        string? libraryVersion,
        string? shadowCommit)
    {
        var gearsById = graph.Gears.ToDictionary(gear => gear.InstanceId, StringComparer.Ordinal);

        return new ShaftGraphReport(
            SchemaVersion: 1,
            Model: new ShaftGraphReportModel(modelFile, rootSection),
            Sources: new ShaftGraphReportSources(libraryVersion, shadowCommit),
            Counts: new ShaftGraphReportCounts(
                graph.Shafts.Length,
                graph.Shafts.Count(shaft => shaft.MemberCount > 1),
                graph.Gears.Length,
                graph.Meshes.Length,
                graph.AmbiguousMeshes.Length,
                graph.UnsupportedComponents.Length,
                graph.UncataloguedComponents.Length),
            Shafts:
            [
                .. graph.Shafts
                    .Where(shaft => shaft.MemberCount > 1)
                    .OrderBy(shaft => shaft.ShaftId, StringComparer.Ordinal)
                    .Select(shaft => new ShaftReportEntry(
                        shaft.ShaftId,
                        shaft.MemberCount,
                        shaft.InstanceIds,
                        Vector(shaft.Origin),
                        Vector(shaft.Axis),
                        shaft.BearingInstanceIds)),
            ],
            Gears:
            [
                .. graph.Gears
                    .OrderBy(gear => gear.InstanceId, StringComparer.Ordinal)
                    .Select(gear => new GearReportEntry(
                        gear.InstanceId,
                        gear.CanonicalPartName,
                        gear.ShaftId,
                        gear.Mechanics.Type,
                        gear.Mechanics.Gear?.Teeth,
                        gear.Mechanics.Worm?.Starts,
                        gear.PitchRadiusLdu,
                        gear.FaceHalfWidthLdu,
                        gear.AxisSource,
                        Vector(gear.Centre),
                        Vector(gear.Axis))),
            ],
            Meshes:
            [
                .. graph.Meshes.Select(mesh => new MeshReportEntry(
                    mesh.GearA,
                    mesh.GearB,
                    gearsById[mesh.GearA].CanonicalPartName,
                    gearsById[mesh.GearB].CanonicalPartName,
                    mesh.ShaftA,
                    mesh.ShaftB,
                    mesh.Kind,
                    gearsById[mesh.GearA].Mechanics.Gear?.Teeth,
                    gearsById[mesh.GearB].Mechanics.Gear?.Teeth,
                    mesh.RatioNumerator,
                    mesh.RatioDenominator,
                    mesh.Sign,
                    $"{(mesh.Sign < 0 ? "-" : "+")}{mesh.RatioNumerator}:{mesh.RatioDenominator}",
                    mesh.CentreDistanceLdu,
                    Nullable(mesh.ExpectedCentreDistanceLdu),
                    Nullable(mesh.CentreResidualLdu),
                    mesh.FaceOverlapLdu,
                    mesh.AxisAngleDegrees,
                    mesh.Confidence,
                    mesh.Rule)),
            ],
            AmbiguousMeshes: graph.AmbiguousMeshes,
            Unsupported:
            [
                .. graph.UnsupportedComponents
                    .GroupBy(component => (component.CanonicalPartName, component.Type, component.Reason))
                    .OrderByDescending(group => group.Count())
                    .ThenBy(group => group.Key.CanonicalPartName, StringComparer.Ordinal)
                    .Select(group => new UnsupportedReportEntry(
                        group.Key.CanonicalPartName,
                        group.Key.Type,
                        group.Count(),
                        group.Key.Reason,
                        [.. group.Select(component => component.InstanceId).OrderBy(id => id, StringComparer.Ordinal)])),
            ],
            Uncatalogued: graph.UncataloguedComponents);
    }

    public static string ToJson(ShaftGraphReport report) =>
        JsonSerializer.Serialize(report, JsonOptions);

    /// <summary>NaN means "this build does not predict the value"; JSON gets null rather than NaN.</summary>
    private static float? Nullable(float value) => float.IsNaN(value) ? null : value;

    private static float[] Vector(System.Numerics.Vector3 value) => [value.X, value.Y, value.Z];
}

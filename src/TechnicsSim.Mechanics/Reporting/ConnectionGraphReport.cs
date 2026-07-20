using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using TechnicsSim.LDraw;
using TechnicsSim.Mechanics.Features;
using TechnicsSim.Mechanics.Mating;

namespace TechnicsSim.Mechanics.Reporting;

public sealed record ConnectionGraphReport(
    int SchemaVersion,
    ConnectionModelReport Model,
    ConnectionSourceReport Sources,
    ConnectionCountReport Counts,
    ImmutableArray<FeatureReport> Features,
    ImmutableArray<ConnectionReport> Connections,
    ImmutableArray<AmbiguityReport> Ambiguities,
    ImmutableArray<ExtractionIssueReport> ExtractionIssues,
    ImmutableArray<RejectedInheritanceReport> RejectedInheritance)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record ConnectionModelReport(string File, string RootSection);

public sealed record ConnectionSourceReport(string? OfficialLibraryVersion, string? ShadowCommit);

public sealed record ConnectionCountReport(
    int LogicalInstances,
    int Features,
    int Connections,
    int UnmatchedFeatures,
    int AmbiguousFeatures,
    int BroadPhasePairs,
    int NarrowPhaseCandidates);

public sealed record FeatureReport(
    string Key,
    string InstanceId,
    string Part,
    string Type,
    string? Id,
    string? Group,
    FeatureOrigin Origin,
    float[] AxisStart,
    float[] AxisEnd,
    float Radius,
    ImmutableArray<CylinderSectionReport> Sections,
    ProvenanceReport Provenance);

public sealed record CylinderSectionReport(string Kind, float Radius, float Length);

public sealed record ProvenanceReport(
    string OfficialFile,
    string ShadowFile,
    int ShadowLine,
    string ClassificationRule,
    ImmutableArray<TransformStepReport> Transforms);

public sealed record TransformStepReport(string File, int Line, string Rule, float[] Matrix);

public sealed record ConnectionReport(
    string FeatureA,
    string FeatureB,
    string InstanceA,
    string InstanceB,
    ConnectionKind Kind,
    ConnectionConfidence Confidence,
    string Rule,
    bool Ambiguous,
    MateResiduals Residuals);

public sealed record AmbiguityReport(string Feature, ImmutableArray<string> Alternatives, string Reason);

public sealed record ExtractionIssueReport(string Part, string? ShadowFile, int Line, string Reason);

public sealed record RejectedInheritanceReport(
    string Part,
    string Feature,
    string DeclaringFile,
    int ReferenceLine,
    string Reason);

public static class ConnectionGraphReportBuilder
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static ConnectionGraphReport Build(
        LoadedModel model,
        ConnectionAnalysis analysis,
        string? officialLibraryVersion = null,
        string? shadowCommit = null) =>
        new(
            ConnectionGraphReport.CurrentSchemaVersion,
            new ConnectionModelReport(Path.GetFileName(model.Path), model.Root.Name),
            new ConnectionSourceReport(officialLibraryVersion, shadowCommit),
            new ConnectionCountReport(
                model.Expansion.Instances.Length,
                analysis.Features.Length,
                analysis.Connections.Length,
                analysis.UnmatchedFeatureKeys.Length,
                analysis.Ambiguities.Length,
                analysis.BroadPhasePairs,
                analysis.NarrowPhaseCandidates),
            analysis.Features.Select(ToReport).ToImmutableArray(),
            analysis.Connections.Select(connection => new ConnectionReport(
                    connection.FeatureA,
                    connection.FeatureB,
                    connection.InstanceA,
                    connection.InstanceB,
                    connection.Kind,
                    connection.Confidence,
                    connection.Rule,
                    connection.IsAmbiguous,
                    connection.Residuals))
                .ToImmutableArray(),
            analysis.Ambiguities.Select(ambiguity => new AmbiguityReport(
                    ambiguity.FeatureKey, ambiguity.CandidateFeatureKeys, ambiguity.Reason))
                .ToImmutableArray(),
            analysis.ExtractionIssues.Select(issue => new ExtractionIssueReport(
                    issue.Part, issue.ShadowFile, issue.LineNumber, issue.Reason))
                .ToImmutableArray(),
            analysis.RejectedInheritance.Select(rejected => new RejectedInheritanceReport(
                    rejected.Part,
                    rejected.FeatureKey,
                    rejected.DeclaringFile,
                    rejected.ReferenceLine,
                    rejected.Reason))
                .ToImmutableArray());

    public static string ToJson(ConnectionGraphReport report) => JsonSerializer.Serialize(report, JsonOptions);

    private static FeatureReport ToReport(PlacedFeature feature)
    {
        var axis = FeatureGeometry.Axis(feature);
        var sections = feature.Feature.Shape is CylinderSnapShape cylinder
            ? cylinder.Sections.Select(section => new CylinderSectionReport(
                    section.Kind.ToString(), section.Radius, section.Length))
                .ToImmutableArray()
            : [];
        var provenance = feature.Feature.Provenance;

        return new FeatureReport(
            feature.Key,
            feature.InstanceId,
            feature.CanonicalPartName,
            feature.Feature.Shape.GetType().Name.Replace("SnapShape", string.Empty, StringComparison.Ordinal),
            feature.Feature.Id,
            feature.Feature.Group,
            feature.Feature.Origin,
            Vector(axis.Start),
            Vector(axis.End),
            axis.Radius,
            sections,
            new ProvenanceReport(
                provenance.OfficialFile,
                provenance.ShadowFile,
                provenance.ShadowLineNumber,
                provenance.ClassificationRule,
                provenance.TransformChain.Select(step => new TransformStepReport(
                        step.File, step.LineNumber, step.Rule, Matrix(step.Transform)))
                    .ToImmutableArray()));
    }

    private static float[] Vector(Vector3 value) => [value.X, value.Y, value.Z];

    private static float[] Matrix(Matrix4x4 value) =>
    [
        value.M11, value.M12, value.M13, value.M14,
        value.M21, value.M22, value.M23, value.M24,
        value.M31, value.M32, value.M33, value.M34,
        value.M41, value.M42, value.M43, value.M44,
    ];
}

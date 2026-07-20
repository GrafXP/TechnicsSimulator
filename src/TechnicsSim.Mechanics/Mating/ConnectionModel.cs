using System.Collections.Immutable;
using TechnicsSim.Mechanics.Features;

namespace TechnicsSim.Mechanics.Mating;

public enum ConnectionKind
{
    KeyedCoaxial,
    RevoluteBearing,
    GeometricMate,
    FixedCandidate,
    TypedJointCandidate,
}

public enum ConnectionConfidence
{
    Low,
    Medium,
    High,
}

public sealed record MateResiduals(
    float RadialLdu,
    float AxialOverlapLdu,
    float AxisAngleDegrees,
    float ProfileClearanceLdu);

public sealed record ConnectionCandidate(
    string FeatureA,
    string FeatureB,
    string InstanceA,
    string InstanceB,
    ConnectionKind Kind,
    ConnectionConfidence Confidence,
    string Rule,
    MateResiduals Residuals,
    bool IsAmbiguous);

public sealed record AmbiguousCandidate(
    string FeatureKey,
    ImmutableArray<string> CandidateFeatureKeys,
    string Reason);

public sealed record MateOptions(
    float RadialToleranceLdu = 0.75f,
    float MinimumAxialOverlapLdu = 0.1f,
    float AxisAngleToleranceDegrees = 2f,
    float GenericPositionToleranceLdu = 1f);

public sealed record ConnectionAnalysis(
    ImmutableArray<PlacedFeature> Features,
    ImmutableArray<ConnectionCandidate> Connections,
    ImmutableArray<string> UnmatchedFeatureKeys,
    ImmutableArray<AmbiguousCandidate> Ambiguities,
    ImmutableArray<FeatureExtractionIssue> ExtractionIssues,
    ImmutableArray<RejectedFeatureInheritance> RejectedInheritance,
    int BroadPhasePairs,
    int NarrowPhaseCandidates)
{
    private readonly IReadOnlyDictionary<string, PlacedFeature> _byKey =
        Features.ToDictionary(feature => feature.Key, StringComparer.Ordinal);

    public PlacedFeature? FindFeature(string key) => _byKey.GetValueOrDefault(key);

    public IEnumerable<ConnectionCandidate> ConnectionsForInstance(string instanceId) =>
        Connections.Where(connection => connection.InstanceA == instanceId || connection.InstanceB == instanceId);
}

using System.Collections.Immutable;
using System.Numerics;
using TechnicsSim.LDraw;
using TechnicsSim.LDraw.Expansion;
using TechnicsSim.LDraw.Resolution;
using TechnicsSim.LDraw.Sources;
using TechnicsSim.Mechanics.Features;

namespace TechnicsSim.Mechanics.Mating;

public static class ModelConnectionAnalyzer
{
    public static ConnectionAnalysis Analyze(
        LoadedModel model,
        ILDrawFileSource shadowSource,
        MateOptions? options = null) =>
        Analyze(model.Expansion, model.Resolver, shadowSource, options);

    public static ConnectionAnalysis Analyze(
        ModelExpansion expansion,
        LDrawResolver resolver,
        ILDrawFileSource shadowSource,
        MateOptions? options = null)
    {
        var extractor = new EffectiveFeatureExtractor(resolver, shadowSource);
        var byPart = new Dictionary<string, PartFeatureExtraction>(StringComparer.Ordinal);

        foreach (var part in expansion.Instances.Select(instance => instance.CanonicalPartName).Distinct(StringComparer.Ordinal))
        {
            byPart[part] = extractor.Extract(part);
        }

        var placed = ImmutableArray.CreateBuilder<PlacedFeature>();
        foreach (var instance in expansion.Instances)
        {
            var extraction = byPart[instance.CanonicalPartName];
            foreach (var feature in extraction.Features)
            {
                placed.Add(new PlacedFeature(
                    $"{instance.InstanceId}/{feature.Key}",
                    instance.InstanceId,
                    instance.CanonicalPartName,
                    feature,
                    Matrix4x4.Multiply(feature.Transform, instance.Transform)));
            }
        }

        return new FeatureMatcher(options).Match(
            placed,
            byPart.Values.SelectMany(result => result.Issues),
            byPart.Values.SelectMany(result => result.RejectedInheritance));
    }
}

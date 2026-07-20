using System.Collections.Immutable;
using System.Numerics;
using TechnicsSim.LDraw.Geometry;
using TechnicsSim.Mechanics.Shafts;
using TechnicsSim.Mechanics.Solver;

namespace TechnicsSim.Wpf.Rendering;

public sealed record ShaftAnimationMember(string InstanceId, Matrix4x4 InitialTransform);

public sealed record ShaftAnimationGroup(
    Vector3 Origin,
    Vector3 Axis,
    ExactRatio AngularVelocity,
    ImmutableArray<ShaftAnimationMember> Members);

/// <summary>
/// Immutable per-model animation data. Building it once keeps graph traversal and the complete
/// scene-instance lookup out of the render-timer hot path.
/// </summary>
public sealed record ShaftAnimationPlan(ImmutableArray<ShaftAnimationGroup> Groups)
{
    public static ShaftAnimationPlan Empty { get; } = new([]);
}

/// <summary>Builds model-space instance transforms for one point on the drivetrain timeline.</summary>
public static class ShaftAnimation
{
    public static ShaftAnimationPlan CreatePlan(
        RenderScene scene,
        ShaftGraph graph,
        ShaftSolution solution)
    {
        if (!solution.IsConsistent)
        {
            return ShaftAnimationPlan.Empty;
        }

        var sceneById = scene.Instances.ToDictionary(instance => instance.InstanceId, StringComparer.Ordinal);

        // Unsupported mechanisms are visible boundaries, not partially animated claims. Motor
        // records describe the stationary housing; the shaft/axle joined to its output rotates.
        var staticInstances = graph.UnsupportedComponents.Select(component => component.InstanceId)
            .Concat(graph.Drivers.Select(driver => driver.InstanceId))
            .ToHashSet(StringComparer.Ordinal);
        var groups = ImmutableArray.CreateBuilder<ShaftAnimationGroup>();

        foreach (var state in solution.Shafts)
        {
            var shaft = graph.FindShaft(state.ShaftId);
            if (shaft is null || shaft.Axis.LengthSquared() < 1e-8f)
            {
                continue;
            }

            var axis = Vector3.Normalize(shaft.Axis);
            var members = ImmutableArray.CreateBuilder<ShaftAnimationMember>();

            foreach (var instanceId in shaft.InstanceIds)
            {
                if (!staticInstances.Contains(instanceId)
                    && sceneById.TryGetValue(instanceId, out var instance))
                {
                    members.Add(new ShaftAnimationMember(instanceId, instance.Transform));
                }
            }

            if (members.Count > 0)
            {
                groups.Add(new ShaftAnimationGroup(
                    shaft.Origin,
                    axis,
                    state.AngularVelocity,
                    members.ToImmutable()));
            }
        }

        return new ShaftAnimationPlan(groups.ToImmutable());
    }

    public static IReadOnlyDictionary<string, Matrix4x4> BuildTransforms(
        ShaftAnimationPlan plan,
        double inputTurns)
    {
        if (Math.Abs(inputTurns) < 1e-12)
        {
            return new Dictionary<string, Matrix4x4>();
        }

        var transforms = new Dictionary<string, Matrix4x4>(StringComparer.Ordinal);
        foreach (var group in plan.Groups)
        {
            var angle = (float)(inputTurns * Math.Tau * group.AngularVelocity.ToDouble());
            var rotation = Matrix4x4.CreateTranslation(-group.Origin)
                * Matrix4x4.CreateFromAxisAngle(group.Axis, angle)
                * Matrix4x4.CreateTranslation(group.Origin);

            foreach (var member in group.Members)
            {
                transforms[member.InstanceId] = member.InitialTransform * rotation;
            }
        }

        return transforms;
    }
}

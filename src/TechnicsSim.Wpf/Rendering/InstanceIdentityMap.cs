using System.Collections.Immutable;

namespace TechnicsSim.Wpf.Rendering;

/// <summary>
/// Maps a rendered instance back to its hierarchical logical instance ID.
///
/// HelixToolkit reports a hit on an instanced model by setting <c>HitTestResult.Tag</c> to the
/// <b>zero-based index of the instance</b> within that model's <c>Instances</c> list. It is not
/// the value from <c>InstanceIdentifiers</c>, despite that property existing and taking GUIDs;
/// this was established by tracing a running renderer rather than from the API shape, which is
/// misleading on the point.
///
/// So the lookup key is (model, instance index), and each registered model carries the ordered
/// instance IDs its batch was built from. Keeping that here rather than inside the renderer is
/// what makes the Phase 1 gate -- clicking a rendered instance yields the correct logical
/// instance ID -- testable without a GPU.
/// </summary>
public sealed class InstanceIdentityMap
{
    private readonly Dictionary<object, ImmutableArray<string>> _byModel =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>Registered instanced models.</summary>
    public int ModelCount => _byModel.Count;

    /// <summary>Total instances across every registered model.</summary>
    public int InstanceCount => _byModel.Values.Sum(v => v.Length);

    /// <summary>
    /// Associates a rendered model with the instance IDs of its batch, in the same order the
    /// instance transforms were supplied.
    /// </summary>
    public void Register(object model, ImmutableArray<string> instanceIds) =>
        _byModel[model] = instanceIds;

    /// <summary>Resolves a hit model and instance index to a logical instance ID.</summary>
    public bool TryResolve(object? model, int instanceIndex, out string instanceId)
    {
        instanceId = string.Empty;

        if (model is null || !_byModel.TryGetValue(model, out var ids))
        {
            return false;
        }

        if (instanceIndex < 0 || instanceIndex >= ids.Length)
        {
            // A stale hit against a model whose batch has since been rebuilt smaller.
            return false;
        }

        instanceId = ids[instanceIndex];
        return true;
    }

    public void Clear() => _byModel.Clear();
}

using System.Collections.Immutable;
using TechnicsSim.Wpf.Rendering;

namespace TechnicsSim.Wpf.Tests;

/// <summary>
/// Covers the Phase 1 gate requirement that clicking a rendered instance yields the correct
/// hierarchical logical instance ID.
///
/// HelixToolkit reports a hit as (model, instance index), so these tests exercise exactly that
/// contract without needing a Direct3D device.
/// </summary>
public sealed class InstanceIdentityMapTests
{
    private static readonly object ModelA = new();
    private static readonly object ModelB = new();

    [Fact]
    public void ResolvesAnInstanceIndexToItsLogicalId()
    {
        var map = new InstanceIdentityMap();
        map.Register(ModelA, ["main.ldr@3", "main.ldr@4", "main.ldr@5"]);

        Assert.True(map.TryResolve(ModelA, 1, out var id));
        Assert.Equal("main.ldr@4", id);
    }

    [Fact]
    public void KeepsIndicesInTheOrderTransformsWereAdded()
    {
        // The index Helix hands back is positional, so any reordering between the transform
        // list and the ID list would silently select the wrong part.
        var ids = ImmutableArray.Create(
            "main.ldr@16|m-1.ldr@252",
            "main.ldr@16|m-1.ldr@253",
            "main.ldr@17|m-1.ldr@252");

        var map = new InstanceIdentityMap();
        map.Register(ModelA, ids);

        for (var i = 0; i < ids.Length; i++)
        {
            Assert.True(map.TryResolve(ModelA, i, out var resolved));
            Assert.Equal(ids[i], resolved);
        }
    }

    [Fact]
    public void KeepsModelsIndependent()
    {
        var map = new InstanceIdentityMap();
        map.Register(ModelA, ["a@1", "a@2"]);
        map.Register(ModelB, ["b@1", "b@2"]);

        Assert.True(map.TryResolve(ModelA, 0, out var a));
        Assert.True(map.TryResolve(ModelB, 0, out var b));
        Assert.Equal("a@1", a);
        Assert.Equal("b@1", b);
        Assert.Equal(2, map.ModelCount);
        Assert.Equal(4, map.InstanceCount);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(int.MaxValue)]
    public void RejectsAnOutOfRangeIndexInsteadOfThrowing(int index)
    {
        // A hit can arrive against a model whose batch has since been rebuilt smaller.
        var map = new InstanceIdentityMap();
        map.Register(ModelA, ["a@1", "a@2"]);

        Assert.False(map.TryResolve(ModelA, index, out _));
    }

    [Fact]
    public void RejectsAnUnknownOrNullModel()
    {
        var map = new InstanceIdentityMap();
        map.Register(ModelA, ["a@1"]);

        Assert.False(map.TryResolve(ModelB, 0, out _));
        Assert.False(map.TryResolve(null, 0, out _));
    }

    [Fact]
    public void DistinguishesModelsByReferenceNotEquality()
    {
        // Two batches can be value-equal yet be different draw calls; identity must follow the
        // object, not its contents.
        var first = new List<int> { 1 };
        var second = new List<int> { 1 };

        var map = new InstanceIdentityMap();
        map.Register(first, ["first"]);
        map.Register(second, ["second"]);

        Assert.True(map.TryResolve(first, 0, out var a));
        Assert.True(map.TryResolve(second, 0, out var b));
        Assert.Equal("first", a);
        Assert.Equal("second", b);
    }

    [Fact]
    public void ClearDropsEverything()
    {
        var map = new InstanceIdentityMap();
        map.Register(ModelA, ["a@1"]);

        map.Clear();

        Assert.Equal(0, map.ModelCount);
        Assert.False(map.TryResolve(ModelA, 0, out _));
    }
}

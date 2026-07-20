using System.Collections.Immutable;
using TechnicsSim.Mechanics.Solver;

namespace TechnicsSim.Wpf.ViewModels;

/// <summary>One input configuration offered by the animation toolbar.</summary>
public sealed record AnimationInputChoice(
    string DisplayName,
    ImmutableArray<ShaftInput> Inputs)
{
    public override string ToString() => DisplayName;
}

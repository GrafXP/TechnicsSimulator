namespace TechnicsSim.LDraw.Geometry;

/// <summary>The polygon winding a file declares its faces to use.</summary>
public enum Winding
{
    CounterClockwise,
    Clockwise,
}

/// <summary>
/// Back-face-culling state while walking one file, per the LDraw BFC extension.
///
/// Two things are tracked separately and are easy to conflate:
///
/// <list type="bullet">
/// <item><see cref="LocalWinding"/> is how the current file declares <em>its own</em> polygons
/// to be wound, set by <c>BFC CERTIFY CCW|CW</c> and changed by later <c>BFC CW|CCW</c> lines.</item>
/// <item><see cref="Inverted"/> accumulates down the reference chain: a reflecting transform
/// (negative determinant) or a <c>BFC INVERTNEXT</c> flips it, and flips compose by XOR.</item>
/// </list>
///
/// A face is emitted front-facing when those two agree, and reversed when they do not.
/// </summary>
public readonly record struct BfcState(
    bool Certified,
    Winding LocalWinding,
    bool Clip,
    bool Inverted)
{
    /// <summary>
    /// The state a file starts in before reading any <c>BFC</c> meta. Uncertified until it says
    /// otherwise, which is what makes unmarked geometry render double-sided rather than being
    /// silently treated as certified.
    /// </summary>
    public static BfcState Initial => new(false, Winding.CounterClockwise, true, false);

    /// <summary>True when faces should be culled; false means render double-sided.</summary>
    public bool ShouldCull => Certified && Clip;

    /// <summary>
    /// True when a polygon's vertices must be reversed to come out counter-clockwise-front.
    /// </summary>
    public bool ReversesWinding => (LocalWinding == Winding.Clockwise) ^ Inverted;

    /// <summary>
    /// The state to use inside a referenced file.
    ///
    /// The child begins uncertified with its own default winding; only the accumulated
    /// inversion and the clip flag carry across the boundary.
    /// </summary>
    public BfcState ForChild(bool transformReflects, bool invertNext) => new(
        Certified: false,
        LocalWinding: Winding.CounterClockwise,
        Clip: Clip,
        Inverted: Inverted ^ transformReflects ^ invertNext);

    /// <summary>Applies one <c>0 BFC ...</c> meta, returning the updated state.</summary>
    /// <param name="invertNext">Set when this meta was <c>INVERTNEXT</c>.</param>
    public BfcState ApplyMeta(string arguments, out bool invertNext)
    {
        invertNext = false;
        var state = this;

        var tokens = arguments.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in tokens)
        {
            switch (raw.ToUpperInvariant())
            {
                case "CERTIFY":
                    state = state with { Certified = true };
                    break;

                case "NOCERTIFY":
                    state = state with { Certified = false };
                    break;

                case "CCW":
                    state = state with { LocalWinding = Winding.CounterClockwise };
                    break;

                case "CW":
                    state = state with { LocalWinding = Winding.Clockwise };
                    break;

                case "CLIP":
                    state = state with { Clip = true };
                    break;

                case "NOCLIP":
                    state = state with { Clip = false };
                    break;

                case "INVERTNEXT":
                    invertNext = true;
                    break;
            }
        }

        // `BFC CCW` and `BFC CW` on their own also certify the file.
        if (!state.Certified
            && tokens.Any(t => t.Equals("CCW", StringComparison.OrdinalIgnoreCase)
                            || t.Equals("CW", StringComparison.OrdinalIgnoreCase)))
        {
            state = state with { Certified = true };
        }

        return state;
    }
}

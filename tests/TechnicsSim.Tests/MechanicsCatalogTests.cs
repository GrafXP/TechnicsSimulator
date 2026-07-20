using TechnicsSim.LDraw;
using TechnicsSim.Mechanics.Catalog;

namespace TechnicsSim.Tests;

/// <summary>
/// Guards the reviewed part-semantics table. The catalog is hand-written data rather than
/// derived output, so the tests that matter are the ones a careless edit would break: schema
/// validity, attribution, and whether it still covers the parts the models actually use.
/// </summary>
public sealed class MechanicsCatalogTests
{
    private static MechanicsCatalog Catalog => CatalogLocator.Load(null, TestEnvironment.RepositoryRoot);

    [Fact]
    public void TheCommittedCatalogLoadsAndValidates()
    {
        var catalog = Catalog;

        Assert.NotEmpty(catalog.Parts);
        Assert.All(catalog.Parts.Values, part => Assert.False(string.IsNullOrWhiteSpace(part.Source)));
    }

    [Fact]
    public void EveryUnsupportedBoundaryExplainsItself()
    {
        // The whole point of the boundary marker is that the UI can say why propagation stopped.
        foreach (var part in Catalog.Parts.Values.Where(p => p.Support == MechanicalSupport.UnsupportedBoundary))
        {
            Assert.False(
                string.IsNullOrWhiteSpace(part.UnsupportedReason),
                $"{part.Part} is an unsupported boundary with no reason.");
        }
    }

    [Fact]
    public void OrdinaryTechnicGearsShareOneModule()
    {
        // The two spacings PLAN.md names: 8:24 sits at 40 LDU (two studs) and 8:40 at 60 LDU
        // (three studs). Both fall out of pitchRadius = teeth * 1.25, which is what makes the
        // derived radius trustworthy enough to omit from the file.
        Assert.Equal(40f, CentreDistance(8, 24), 3);
        Assert.Equal(60f, CentreDistance(8, 40), 3);
        Assert.Equal(40f, CentreDistance(16, 16), 3);

        static float CentreDistance(int a, int b) =>
            new GearGeometry(a).EffectivePitchRadiusLdu + new GearGeometry(b).EffectivePitchRadiusLdu;
    }

    [Fact]
    public void GearEntriesDeriveTheirPitchRadiusUnlessTheySayOtherwise()
    {
        var gear = Catalog.Find("3648b.dat");

        Assert.NotNull(gear);
        Assert.Equal(24, gear!.Gear!.Teeth);
        Assert.Null(gear.Gear.PitchRadiusLdu);
        Assert.Equal(30f, gear.Gear.EffectivePitchRadiusLdu, 3);
    }

    [Fact]
    public void TheKnobGearIsABoundaryRatherThanAGuessedPitchRadius()
    {
        // Deriving a knob gear's pitch radius from its tooth count would give 5 LDU. Marking it
        // unsupported is the honest outcome until someone measures a real pair.
        var knob = Catalog.Find("32072.dat");

        Assert.NotNull(knob);
        Assert.Equal(MechanicalComponentType.KnobGear, knob!.Type);
        Assert.Equal(MechanicalSupport.UnsupportedBoundary, knob.Support);
    }

    [Fact]
    public void WormSemanticsCarryStartsButClaimNothingAboutBackdriving()
    {
        var worm = Catalog.Find("4716.dat");

        Assert.NotNull(worm);
        Assert.Equal(MechanicalComponentType.WormGear, worm!.Type);
        Assert.Equal(1, worm.Worm!.Starts);
        Assert.Null(worm.Gear);
    }

    [Theory]
    [InlineData("58120.dat", "PF Medium Motor")]
    [InlineData("58121.dat", "PF XL Motor")]
    public void MotorsCarryAnInputLabel(string part, string label)
    {
        var motor = Catalog.Find(part);

        Assert.NotNull(motor);
        Assert.Equal(MechanicalComponentType.Motor, motor!.Type);
        Assert.Equal(label, motor.Motor!.DefaultInputLabel);
    }

    /// <summary>
    /// The catalog exists to cover the supplied models, so drifting out of step with them is the
    /// failure worth catching. This pins the toothed parts each model actually uses.
    /// </summary>
    [RealLibraryTheory]
    [InlineData("8275-1.mpd", 9)]
    [InlineData("42055-1.mpd", 16)]
    [InlineData("42100-1.mpd", 12)]
    [InlineData("42121-1.mpd", 7)]
    public void EveryToothedPartInEveryModelIsCatalogued(string fileName, int expectedToothedParts)
    {
        var model = ModelLoader.Load(
            Path.Combine(TestEnvironment.ModelsDirectory, fileName),
            [TestEnvironment.Library!]);
        var catalog = Catalog;

        // Any part whose official description names it a gear, worm, or turntable must have an
        // entry. Matching on the description keeps this honest when a new model is added: the
        // test discovers the gap instead of trusting a hand-maintained list.
        var toothed = model.Expansion.PartUsage.Keys
            .Where(part => IsToothedByDescription(model, part))
            .OrderBy(part => part, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Pinning the count stops this passing vacuously. If descriptions ever stop resolving,
        // the sweep finds nothing, every part is trivially covered, and the real check is gone.
        Assert.Equal(expectedToothedParts, toothed.Count);

        var missing = toothed.Where(part => catalog.Find(part) is null).ToList();

        Assert.True(
            missing.Count == 0,
            $"{fileName} uses toothed parts with no catalog entry: {string.Join(", ", missing)}");
    }

    private static bool IsToothedByDescription(LoadedModel model, string part)
    {
        var description = model.Resolver.Resolve(part).Document?.Description;
        if (string.IsNullOrWhiteSpace(description) || description.StartsWith('~'))
        {
            return false;
        }

        return description.Contains("Gear", StringComparison.OrdinalIgnoreCase)
            || description.Contains("Worm", StringComparison.OrdinalIgnoreCase)
            || description.Contains("Turntable", StringComparison.OrdinalIgnoreCase);
    }
}

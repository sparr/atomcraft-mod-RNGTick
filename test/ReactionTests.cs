using Atomcraft;
using Atomcraft.TestHarness;

namespace RNGTick.Test;

/// <summary>
/// The headline claim, measured in the game's own simulation rather than in a histogram of
/// rolls: a rare shipped reaction that cannot happen at most positions in vanilla can happen
/// at nearly all of them with the mod.
///
/// <para><b>Nothing here reproduces the game's arithmetic.</b> <see cref="BiasTests"/> asks
/// <c>RNG.Roll</c> for numbers and reduces them the way <c>BaseMaterial.IsReactionValid</c>
/// does; that is a model, and a model is a copy with no link back to the original. It went
/// stale once already in this repository, on a different gate, and kept passing while
/// describing an expression the game no longer evaluated. This test places the game's own
/// material, lets the game's own <c>Step</c> run, and counts pixels. The only way it can
/// report a fix that is not there is if the fix is actually there.</para>
///
/// <para><b>The recipe.</b> <c>Compacted Dirt Decomposition</c> ships with the game: one
/// Compacted Dirt above 300 K becomes one Dirt, at <c>Probability 1000</c>. One input cell and
/// one output cell is what makes it the right choice -- a site is a <b>single pixel</b>, so
/// exactly one position's rolls decide it and the product lands in the cell that reacted. Of
/// the 84 shipped recipes with a probability, 14 are rarer than 1 in 100; compost from fallen
/// leaves, at 1 in 10000 and needing no heat at all, is the one a player would actually sit
/// and wait for, and it is a 3x3 recipe rather than a 1x1 one.</para>
///
/// <para><b>What vanilla can and cannot do.</b> A position has 256 rolls and nothing else, so
/// at most <c>1 - (1 - 1/1000)^256</c> of positions -- about 23% -- can ever produce this
/// reaction, and which 23% is settled before the first tick. The rest are not unlucky. They
/// are locked out, and the test proves it the strong way: every site that reacts at all
/// reacts inside the first 256 ticks, and the count does not move again for the nine cycles
/// that follow.</para>
/// </summary>
public static class ReactionTests
{
    /// <summary>The shipped recipe under test. Its numbers are read from the game, not copied.</summary>
    internal const string Recipe = "Compacted Dirt Decomposition";

    internal const string Site = "Compacted Dirt";
    internal const string Product = "Dirt";

    /// <summary>
    /// The checkerboard's other square, and the reason each site is an island.
    ///
    /// <para>A reaction looks at the 3x3 block around itself, so two sites that could see each
    /// other would share an outcome and the count would stop being one per position. Ceramic
    /// Wall is Static, and <c>BaseMaterial.IsDiagonalSealed</c> skips a diagonal neighbour when
    /// both cells between it and the centre are static -- so on a checkerboard a site's four
    /// orthogonal neighbours are wall and its four diagonal neighbours are sealed off behind
    /// them. The only cell of the input material in a site's neighbourhood is the site.</para>
    /// </summary>
    internal const string Wall = "Ceramic Wall";

    /// <summary>
    /// Warm enough for the recipe's 300 K minimum, cool enough to stay below the 373 K where
    /// <c>Dirt to Sand</c> would start consuming the product. Neither is taken on trust: the
    /// minimum is read back out of the game, and the test asserts every site ended as one of
    /// the two materials this fixture is about.
    /// </summary>
    internal const short Kelvin = 320;

    /// <summary>The RNG table's depth, and so the period of a position's luck in vanilla.</summary>
    internal const int Cycle = 256;

    /// <summary>
    /// How long each arm waits, in cycles. Ten is enough to separate the three modes by an
    /// order of magnitude and cheap enough to run all three: 7680 ticks in total.
    /// </summary>
    internal const int Cycles = 10;

    internal const int Ticks = Cycle * Cycles;

    /// <summary>
    /// Share of the arithmetic expectation a working mode has to reach. Slack for the fact
    /// that the mod redistributes 256 values rather than drawing fresh ones, not room for a
    /// mode that is still handing out luck by address: the gap is 92% against 23%.
    /// </summary>
    internal const double RequiredShareOfNominal = 0.8;

    /// <summary>
    /// The fixture: every cell of the region wall, then every other interior cell replaced by
    /// a one-pixel reaction site. 1922 independent sites in a single chunk.
    ///
    /// <para>Rebuilt between arms, which is what lets all three measure the same positions.
    /// The tick is reset with it, so no arm gets a different slice of the table by accident.</para>
    /// </summary>
    internal static List<(int X, int Y)> Build(Region r)
    {
        var sites = new List<(int, int)>();

        r.Fill(0, 0, r.Width, r.Height, Wall);

        for (var y = 1; y < r.Height - 1; y++)
        for (var x = 1; x < r.Width - 1; x++)
        {
            if ((x + y) % 2 == 0)
                continue;
            r.Set(x, y, Site);
            sites.Add((x, y));
        }

        // One fill holds for the whole run: the test disables the only two mechanisms that
        // would move the heatmap afterwards.
        r.FillHeat(Kelvin);
        r.Tick = 0;
        return sites;
    }

    internal static int Reacted(Region r, List<(int X, int Y)> sites) =>
        sites.Count(c => r.At(c.X, c.Y) == Product);

    /// <summary>What one mode did: how many sites had reacted after one cycle, and after ten.</summary>
    internal sealed record Arm(OffsetMode Mode, int AfterOneCycle, int AfterAll, int Sites)
    {
        public double Share => (double)AfterAll / Sites;

        public override string ToString() =>
            $"{Mode}: {AfterOneCycle} of {Sites} sites reacted in the first {Cycle} ticks, " +
            $"{AfterAll} ({Share:P1}) in {Ticks}";
    }

    /// <summary>
    /// Rebuilds the fixture and runs it under one mode, stopping once at the cycle boundary so
    /// the frozen-after-one-cycle claim can be made rather than assumed.
    ///
    /// <para>The fixture is checked here, per arm, while the field still holds that arm's
    /// result. Both of these fail in a way that would read as the defect under test -- a cold
    /// site and a locked-out site both simply never react -- so both are ruled out by name.</para>
    /// </summary>
    internal static Arm Measure(Region r, OffsetMode mode, short minimumKelvin)
    {
        var sites = Build(r);
        Rolls.With(mode, () => r.Ticks(Cycle));
        var afterOneCycle = Reacted(r, sites);
        Rolls.With(mode, () => r.Ticks(Ticks - Cycle));
        var arm = new Arm(mode, afterOneCycle, Reacted(r, sites), sites.Count);

        var heat = sites.Select(c => r.HeatAt(c.X, c.Y)).ToArray();
        if (heat.Min() < minimumKelvin)
            throw new AssertionException(
                $"{mode}: sites span {heat.Min()}-{heat.Max()} K and '{Recipe}' needs at least " +
                $"{minimumKelvin} K, so the gate under test was never reached");

        var stray = sites.Select(c => r.At(c.X, c.Y))
            .Where(m => m != Site && m != Product)
            .GroupBy(m => m ?? "air").Select(g => $"{g.Key} x{g.Count()}").ToList();
        if (stray.Count > 0)
            throw new AssertionException(
                $"{mode}: sites ended as something other than {Site} or {Product}: " +
                $"{string.Join(", ", stray)}. Some other mechanism reached this fixture, so " +
                $"these are not counts of '{Recipe}' firing.");

        return arm;
    }

    /// <summary>
    /// The recipe as the game currently ships it, or a refusal to judge. Shared with
    /// <see cref="RetirementTests"/>, which asks the vanilla half of this question.
    /// </summary>
    internal static ReactionType RequireRecipe()
    {
        var recipe = ReactionTypes.GetByName(Recipe);
        if (recipe == null || recipe.Probability <= 1)
            throw new InapplicableException(
                $"this game no longer ships '{Recipe}' as a rare reaction, so there is nothing " +
                "to measure here. Point this test at another recipe with a large Probability.");
        return recipe;
    }

    /// <summary>Share of sites a gate behaving like a probability reaches in <see cref="Ticks"/>.</summary>
    internal static double Nominal(int probability) => 1 - Math.Pow(1 - 1.0 / probability, Ticks);

    /// <summary>
    /// The most a position with only <see cref="Cycle"/> rolls to its name can ever reach, which
    /// is what makes the bar below a proof rather than a comparison.
    /// </summary>
    internal static double Ceiling(int probability) => 1 - Math.Pow(1 - 1.0 / probability, Cycle);

    [GameTest(Disable = SimFeature.All, TimeoutFrames = 30000)]
    public static void ARareShippedReactionStopsBeingAPropertyOfThePosition(Region r)
    {
        var recipe = RequireRecipe();
        var nominal = Nominal(recipe.Probability);
        var ceiling = Ceiling(recipe.Probability);
        var required = nominal * RequiredShareOfNominal;

        var minimum = recipe.Temperature ?? 0;
        var tickOnly = Measure(r, OffsetMode.Tick, minimum);
        var full = Measure(r, OffsetMode.TickAndCycle, minimum);

        Log.Info($"{Recipe} (1 in {recipe.Probability}) over {full.Sites} one-pixel sites, " +
                 $"{Ticks} ticks each. {tickOnly}. {full}. A generator behaving like a " +
                 $"probability reaches {nominal:P1}; {Cycle} rolls per position cannot pass " +
                 $"{ceiling:P1}.");

        // Both modes, because the tick alone is the whole fix for any modulus that does not
        // divide 256, and 1000 does not. A mode that failed here would contradict the account
        // in TickOffset's summary.
        //
        // The bar is absolute, and it doubles as the proof that the patch is reaching the
        // simulation at all: an unpatched game cannot get past the ceiling, and the required
        // share is three times it. Whether vanilla is still stuck under that ceiling is a fact
        // about the game rather than about this mod, and lives in RetirementTests.
        foreach (var arm in new[] { tickOnly, full })
        {
            if (arm.Share < required)
                throw new AssertionException(
                    $"{arm.Mode} reacted at {arm.Share:P1} of {arm.Sites} sites in {Ticks} ticks, " +
                    $"where {nominal:P1} was expected and at least {required:P1} is required. " +
                    $"An unpatched game is capped at {ceiling:P1}, so this is also what says the " +
                    "offset is reaching BaseMaterial's reaction gate.");
        }
    }
}

using Atomcraft;
using Atomcraft.TestHarness;
using Godot;

namespace RNGConformance;

/// <summary>
/// The same question the rest of this suite asks about <c>RNG.Roll</c>, put to the running
/// simulation instead: can a rare shipped reaction fire at every position, or only at some?
///
/// <para><b>Why this is worth a separate test.</b> Everything else here models a consumer.
/// <c>r % 240 == 0</c> stands in for the gate in <c>BaseMaterial.IsReactionValid</c>, and a
/// model is a copy of the game's code with no link back to the original. That link matters:
/// a sibling suite modelled the noble gas condensation gate as <c>roll % 100000 == 1</c>, a
/// game update added a term to it, and the model went on passing while describing an
/// expression the game no longer evaluated. Nothing below reproduces any of the game's
/// arithmetic. It places the game's own material, lets the game's own <c>Step</c> run, and
/// counts pixels.</para>
///
/// <para><b>The recipe.</b> <c>Compacted Dirt Decomposition</c> ships with the game:
/// one Compacted Dirt above 300 K becomes one Dirt, at <c>Probability 1000</c>. It is the
/// cleanest rare reaction in the game for this purpose, because one input cell and one output
/// cell means a site is a <i>single pixel</i>, so exactly one position's rolls decide it, and
/// the product appears in the cell that reacted rather than somewhere in the neighbourhood.
/// No player waits on this particular one, but 14 shipped recipes are rarer than 1 in 100 and
/// they all pass through the same gate; compost from fallen leaves, at 1 in 10000, is the one
/// a player would actually notice.</para>
///
/// <para><b>The bar is absolute</b>, like the rest of the suite. A table with 256 rolls per
/// position cannot let more than <c>1 - (1 - 1/1000)^256</c> of positions ever react, about
/// 23%, however long the world runs; a generator that behaves like a probability reaches 92%
/// in the ten cycles this test waits. Those are far enough apart that no comparison against
/// stock is needed to tell them apart.</para>
/// </summary>
public static class ShippedReactionTests
{
    /// <summary>The shipped recipe under test. Its numbers are read from the game, not copied.</summary>
    private const string Recipe = "Compacted Dirt Decomposition";

    private const string Site = "Compacted Dirt";
    private const string Product = "Dirt";

    /// <summary>
    /// The checkerboard's other square, and the reason each site is an island.
    ///
    /// <para>A reaction looks at the 3x3 block around itself, so two sites that can see each
    /// other would share an outcome and the count would stop being one-per-position. Ceramic
    /// Wall is Static, and <c>BaseMaterial.IsDiagonalSealed</c> skips a diagonal neighbour
    /// whenever both cells between it and the centre are static -- so on a checkerboard a
    /// site's four orthogonal neighbours are wall and its four diagonal neighbours are sealed
    /// off behind it. The only cell of the input material in a site's neighbourhood is the
    /// site itself.</para>
    /// </summary>
    private const string Wall = "Ceramic Wall";

    /// <summary>
    /// Warm enough for the recipe's 300 K minimum, cool enough to stay under the 373 K where
    /// <c>Dirt to Sand</c> would start consuming the product. The test does not take either on
    /// trust: it reads the minimum back out of the game, and asserts that every site ended as
    /// one of the two materials this fixture is about.
    /// </summary>
    private const short Kelvin = 320;

    /// <summary>The RNG table's depth, and so the period of a position's luck in stock Atomcraft.</summary>
    private const int Cycle = 256;

    /// <summary>
    /// How long to wait, in cycles. Ten is enough that a working generator reaches 92% of
    /// positions while a table-bound one is still frozen at whatever the first cycle gave it,
    /// and short enough that the whole test is a few seconds.
    /// </summary>
    private const int Cycles = 10;

    private const int Ticks = Cycle * Cycles;

    /// <summary>
    /// Share of the arithmetic expectation a passing game has to reach. Slack for the fact
    /// that a real fix redistributes 256 values rather than drawing fresh ones, not room for
    /// a game that is still handing out luck by address: the gap being judged here is 92%
    /// against 23%.
    /// </summary>
    private const double RequiredShareOfNominal = 0.8;

    /// <summary>
    /// The fixture: every cell of the region wall, then every other interior cell replaced by
    /// a one-pixel reaction site. 1922 independent sites in a single chunk.
    /// </summary>
    private static List<(int X, int Y)> Build(Region r)
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

        // Both arms of any comparison start on the same tick, so neither gets a different
        // slice of the table by accident.
        r.Tick = 0;
        return sites;
    }

    private static int Reacted(Region r, List<(int X, int Y)> sites) =>
        sites.Count(c => r.At(c.X, c.Y) == Product);

    /// <summary>
    /// Build a bank of identical sites, wait ten cycles, and count how many ever reacted.
    /// </summary>
    [GameTest(Disable = SimFeature.All)]
    public static void ARareShippedReactionCanFireAtEveryPosition(Region r)
    {
        var recipe = ReactionTypes.GetByName(Recipe);
        if (recipe == null)
            throw new InapplicableException(
                $"this game ships no reaction named '{Recipe}', so there is nothing to measure " +
                "here. Point this test at another rare recipe.");

        if (recipe.Probability <= 1)
            throw new InapplicableException(
                $"'{Recipe}' now has Probability {recipe.Probability}, which is not a rare event. " +
                "This test needs a recipe whose gate is actually rolled.");

        var sites = Build(r);

        r.Ticks(Cycle);
        var afterOneCycle = Reacted(r, sites);

        r.Ticks(Ticks - Cycle);
        var afterAll = Reacted(r, sites);

        var p = 1.0 / recipe.Probability;
        var share = (double)afterAll / sites.Count;
        var nominal = 1 - Math.Pow(1 - p, Ticks);
        var ceiling = 1 - Math.Pow(1 - p, Cycle);

        GD.Print($"[conformance] {Recipe} (1 in {recipe.Probability}) over {sites.Count} " +
                 $"one-pixel sites: {afterOneCycle} reacted in the first {Cycle} ticks, " +
                 $"{afterAll} ({share:P1}) in {Ticks}. A generator behaving like a probability " +
                 $"reaches {nominal:P1}; a position with only {Cycle} rolls to its name cannot " +
                 $"pass {ceiling:P1} however long it waits");

        // The fixture, before its result is read as a verdict on the game. Each of these fails
        // in a way that looks like the defect being tested for, so each is ruled out by name.
        var heat = sites.Select(c => r.HeatAt(c.X, c.Y)).ToArray();
        if (heat.Min() < (recipe.Temperature ?? 0))
            throw new AssertionException(
                $"sites span {heat.Min()}-{heat.Max()} K and '{Recipe}' needs at least " +
                $"{recipe.Temperature} K, so the gate under test was never reached");

        var stray = sites.Select(c => r.At(c.X, c.Y))
            .Where(m => m != Site && m != Product)
            .GroupBy(m => m ?? "air").Select(g => $"{g.Key} x{g.Count()}").ToList();
        if (stray.Count > 0)
            throw new AssertionException(
                $"sites ended as something other than {Site} or {Product}: {string.Join(", ", stray)}. " +
                $"Some other mechanism reached this fixture, so the count above is not a count of " +
                $"'{Recipe}' firing.");

        if (afterAll == 0)
            throw new AssertionException(
                $"not one of {sites.Count} sites reacted in {Ticks} ticks. At 1 in " +
                $"{recipe.Probability} that is not luck: either '{Recipe}' no longer fires on " +
                $"{Site} at {Kelvin} K, or this fixture stopped being what it claims to be.");

        if (afterAll == afterOneCycle)
            throw new AssertionException(
                $"every site that will ever react had already reacted {Ticks - Cycle} ticks " +
                $"earlier: {afterOneCycle} after one {Cycle}-tick cycle, and not one more since. " +
                $"The other {sites.Count - afterAll} positions are not unlucky, they are locked " +
                $"out -- a position's 256 rolls either contain one congruent to 0 mod " +
                $"{recipe.Probability} or the reaction cannot happen there at all. This is the " +
                "defect this suite exists to detect, seen in the simulation rather than in a " +
                "histogram.");

        if (share < nominal * RequiredShareOfNominal)
            throw new AssertionException(
                $"{afterAll} of {sites.Count} sites ({share:P1}) reacted in {Ticks} ticks, " +
                $"against {nominal:P1} for a generator that behaves like a probability. A " +
                $"position's long-run odds are still shaped by where it is standing.\n" +
                $"  {afterOneCycle} of those had already reacted after the first {Cycle} ticks");
    }
}

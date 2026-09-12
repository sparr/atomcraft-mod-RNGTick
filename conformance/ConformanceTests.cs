using Atomcraft;
using Atomcraft.TestHarness;
using Godot;
using HarmonyLib;

namespace RNGConformance;

/// <summary>
/// Does Atomcraft's deterministic RNG behave like a probability, or like a property of the
/// address?
///
/// <para><b>What this is for.</b> Nothing here names a mod. Install it next to any candidate fix
/// and it reaches a verdict; install it on its own and it fails, because the stock game does not
/// pass. It is equally valid against a version of the game that has fixed this itself, which is
/// why the assertions that matter are <b>absolute</b> rather than comparisons against stock:
/// there is no stock to compare against once the stock is the thing being judged.</para>
///
/// <para><b>The bug being tested for.</b> <c>RNG.Roll(x, y, tick)</c> indexes a fixed table at
/// <c>tick &amp; 0xFF</c>, so a pixel that does not move has 256 rolls and nothing but those 256,
/// forever. Whatever a caller reduces them to is lumpy in a way that never averages out, and at
/// many positions a reaction simply never fires. The fix, whoever ships it, has to make a
/// position's long-run odds its nominal odds.</para>
///
/// <para><b>How the bar is set.</b> A fair generator still does not give every position the same
/// rate: sampling 32768 ticks of a one-in-240 event leaves a spread of about
/// <c>sqrt(p(1-p)/n)</c> between positions, and no fix can do better than that. So the tests ask
/// for a spread within a small multiple of that floor rather than for equality. Stock Atomcraft
/// sits ten to thirteen times above it, so the two are not close.</para>
/// </summary>
public static class ConformanceTests
{
    /// <summary>
    /// Ticks each position is sampled over. 128 repeats of the table's 256-tick cycle, about nine
    /// minutes of game time -- long enough that a fair generator has settled and a broken one has
    /// had every chance it is ever going to get.
    /// </summary>
    private const int Ticks = 32768;

    /// <summary>Positions across the table, on a stride coprime with its 512-cell width.</summary>
    private static List<(int X, int Y)> Positions()
    {
        var positions = new List<(int, int)>();
        for (var x = 0; x < 512; x += 45)
        for (var y = 0; y < 512; y += 45)
            positions.Add((x, y));
        return positions;
    }

    /// <summary>The spread between positions a fair generator would still leave at this sample size.</summary>
    private static double SamplingFloor(double p) => Math.Sqrt(p * (1 - p) / Ticks);

    private static double StdDev(List<double> values)
    {
        var mean = values.Average();
        return Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / values.Count);
    }

    /// <summary>Per-position rate of some event, measured over <see cref="Ticks"/>.</summary>
    private static List<double> RatesPerPosition(Func<int, bool> fires)
    {
        var rates = new List<double>();
        foreach (var (x, y) in Positions())
        {
            var hits = 0;
            for (var tick = 0; tick < Ticks; tick++)
                if (fires(RNG.Roll(x, y, tick)))
                    hits++;
            rates.Add((double)hits / Ticks);
        }
        return rates;
    }

    /// <summary>
    /// Reports on one event and asserts it behaves like a probability: nobody is locked out, the
    /// average is the nominal rate, and the spread between positions is sampling noise rather
    /// than geography.
    /// </summary>
    private static void AssertBehavesLikeAProbability(string what, double nominal, Func<int, bool> fires)
    {
        var rates = RatesPerPosition(fires);
        var spread = StdDev(rates);
        var floor = SamplingFloor(nominal);
        var dead = rates.Count(r => r == 0);

        GD.Print($"[conformance] {what}: mean {rates.Average():F5} (nominal {nominal:F5}), " +
                 $"spread {spread:F5} against a sampling floor of {floor:F5} ({spread / floor:F1}x), " +
                 $"min {rates.Min():F5}, max {rates.Max():F5}, never-fires {dead}/{rates.Count}");

        if (dead > 0)
            throw new AssertionException(
                $"{what}: {dead} of {rates.Count} positions never produced this outcome once in " +
                $"{Ticks} ticks. At a nominal {nominal:F5} that is not bad luck, it is a position " +
                "that cannot produce it at all -- the defect this suite exists to detect.");

        if (Math.Abs(rates.Average() - nominal) > nominal * 0.25)
            throw new AssertionException(
                $"{what}: the average rate across positions is {rates.Average():F5} against a " +
                $"nominal {nominal:F5}. A fix is supposed to redistribute luck, not create or " +
                "destroy it.");

        // Three times the floor. A fair generator sits at 1.0x and stock Atomcraft at 10x or more,
        // so this separates them with room to spare rather than policing small differences.
        if (spread > floor * 3)
            throw new AssertionException(
                $"{what}: the spread between positions is {spread:F5}, {spread / floor:F1} times the " +
                $"{floor:F5} that sampling {Ticks} ticks would leave on its own. A position's odds " +
                "still depend on where it is standing.\n" +
                $"  worst {rates.Min():F5}, best {rates.Max():F5}, nominal {nominal:F5}");
    }

    /// <summary>
    /// A reaction probability. <c>BaseMaterial</c> fires one on <c>Roll(..) % Probability == 0</c>,
    /// and 240 is a value the shipped recipes actually use.
    /// </summary>
    [GameTest]
    public static void AReactionProbabilityBehavesLikeAProbability() =>
        AssertBehavesLikeAProbability("reaction at Probability 240", 1.0 / 240, r => r % 240 == 0);

    /// <summary>
    /// A percentage check. <c>RNG.RollPct</c> is <c>Roll(..) &amp; 0x7F</c> and is the most-used
    /// roll in the game.
    ///
    /// <para>Worth testing separately from the reaction above, because 128 divides 256 and that
    /// makes it the harder case: a fix that only adds the tick leaves this one exactly as it
    /// found it, since <c>tick % 128</c> is already decided by the <c>tick &amp; 0xFF</c> that
    /// chose the value. A candidate can pass the reaction test and fail this one.</para>
    /// </summary>
    [GameTest]
    public static void APercentageCheckBehavesLikeAProbability() =>
        AssertBehavesLikeAProbability("RollPct below 5 in 128", 5.0 / 128, r => (r & 0x7F) < 5);

    /// <summary>The simplest case, and the one a player would notice: which way does it go.</summary>
    [GameTest]
    public static void ACoinFlipIsFairAtEveryPosition() =>
        AssertBehavesLikeAProbability("coin flip", 0.5, r => r % 2 == 0);

    /// <summary>
    /// Whatever the fix is, a roll is still a number the game's own callers can use.
    ///
    /// <para><c>RNG.RandomDeterministic</c> feeds <c>Roll(..) % array.Length</c> straight into an
    /// indexer and <c>RollFloat</c> feeds <c>Roll(..) &amp; 0x3FF</c> into another, so a roll
    /// outside <c>[0, int.MaxValue)</c> would not skew a probability, it would throw out of the
    /// middle of a parallel simulation pass.</para>
    /// </summary>
    [GameTest]
    public static void RollsStayWithinWhatTheGamesCallersAssume()
    {
        var ticks = new[] { 0, 1, 255, 256, 65535, 1_000_000, int.MaxValue / 2, int.MaxValue - 1, int.MaxValue };

        foreach (var (x, y) in Positions())
        foreach (var tick in ticks)
        {
            var roll = RNG.Roll(x, y, tick);
            if (roll < 0 || roll >= int.MaxValue)
                throw new AssertionException(
                    $"Roll({x},{y},{tick}) returned {roll}, outside [0,{int.MaxValue}) -- the range " +
                    "Random.Next() promises and every caller in the game was written against");

            if (RNG.Roll(new Vector2I(x, y), tick) != roll)
                throw new AssertionException(
                    $"the two Roll overloads disagree at ({x},{y},{tick}); one of them is unfixed");

            if (RNG.RollPct(x, y, tick) != (roll & 0x7F))
                throw new AssertionException(
                    $"RollPct({x},{y},{tick}) disagrees with Roll(..)&0x7F; a wrapper is reaching a " +
                    "different implementation than Roll is");

            var unit = RNG.RollFloat(x, y, tick);
            if (unit <= 0f || unit >= 1f)
                throw new AssertionException($"RollFloat({x},{y},{tick}) returned {unit}");
        }
    }

    /// <summary>
    /// Determinism, which every fix has to preserve: multiplayer clients agree without exchanging
    /// anything, and a cell may ask for its roll more than once in a tick.
    /// </summary>
    [GameTest]
    public static void RollsAreAFunctionOfPositionAndTick()
    {
        foreach (var (x, y) in Positions())
        for (var tick = 0; tick < 16; tick++)
        {
            var first = RNG.Roll(x, y, tick);
            for (var repeat = 0; repeat < 3; repeat++)
                if (RNG.Roll(x, y, tick) != first)
                    throw new AssertionException(
                        $"Roll({x},{y},{tick}) is not a function of its arguments");
        }
    }

    /// <summary>
    /// The one comparison against stock, and the only test here that can be inapplicable.
    ///
    /// <para>Stock <c>RNG.Roll</c> is a lookup into <c>RNGVolume</c> and nothing else, so while
    /// that table exists the unmodified answer is recoverable and a mod that is not reaching the
    /// RNG at all can be told apart from one that is. That distinction is worth having: a Harmony
    /// patch on <c>Roll</c> can report itself installed and never run, because both overloads
    /// carry <c>AggressiveInlining</c>.</para>
    ///
    /// <para>If the table is gone, the game has changed its RNG rather than had it patched, and
    /// this test has nothing to say. It says so and passes: the absolute tests above are what
    /// judge that case.</para>
    /// </summary>
    [GameTest]
    public static void SomethingIsActuallyReachingTheRoll()
    {
        var volume = (int[]?)AccessTools.Field(typeof(RNG), "RNGVolume")?.GetValue(null);
        if (volume == null)
        {
            GD.Print("[conformance] no RNGVolume to compare against; this game's RNG is not the " +
                     "stock lookup, so the absolute tests are the whole verdict");
            return;
        }

        var moved = 0;
        var total = 0;
        foreach (var (x, y) in Positions())
        for (var tick = 0; tick < 64; tick++)
        {
            total++;
            var stock = volume[((x & 0x1FF) * 512 + (y & 0x1FF)) * 256 + (tick & 0xFF)];
            if (RNG.Roll(x, y, tick) != stock)
                moved++;
        }

        GD.Print($"[conformance] {moved}/{total} rolls differ from the stock table");

        if (moved == 0)
            throw new AssertionException(
                $"all {total} rolls matched the stock lookup exactly, so nothing installed is " +
                "affecting RNG.Roll. If a mod is meant to be, note that a Harmony patch on Roll " +
                "can report itself installed and still never run: both overloads carry " +
                "AggressiveInlining, and a caller compiled with the original body pasted in never " +
                "reaches the detour.");
    }
}

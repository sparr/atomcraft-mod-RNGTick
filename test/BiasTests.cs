using Atomcraft;
using Atomcraft.TestHarness;

namespace RNGTick.Test;

/// <summary>
/// The measurements the mod exists for, taken from the live game rather than reasoned about.
///
/// <para>Each test picks one shape of consumer -- a reaction probability, a percentage check,
/// a coin flip -- and measures, for each of 144 positions, how often the event actually
/// happens across 32768 consecutive ticks. What matters is not the average, which is fine in
/// vanilla, but the <i>spread between positions</i>: whether a pixel's odds depend on where
/// it is standing. The vanilla figures are asserted too, so if a game update ever changes the
/// RNG's shape these fail loudly instead of quietly measuring nothing.</para>
///
/// <para>32768 ticks is 128 repeats of the volume's 256-tick cycle, about nine minutes of
/// game time at 60 ticks per second. Long enough that a fair generator would have evened out
/// and short enough that a player would notice it had not.</para>
/// </summary>
public static class BiasTests
{
    /// <summary>Ticks measured per position. 128 full cycles of the 256-deep RNG volume.</summary>
    public const int Ticks = 32768;

    /// <summary>
    /// How often an event fires at each position, as a fraction of <see cref="Ticks"/>.
    /// </summary>
    private sealed class Spread
    {
        public required string Label { get; init; }
        public required double Mean { get; init; }
        public required double StdDev { get; init; }
        public required double Min { get; init; }
        public required double Max { get; init; }

        /// <summary>Positions where the event did not happen once in the whole window.</summary>
        public required int Never { get; init; }

        public required int Positions { get; init; }

        /// <summary>
        /// The per-position rates the summary was computed from, in <c>Rolls.Positions()</c>
        /// order so that two spreads can be compared position by position.
        /// </summary>
        public required double[] Rates { get; init; }

        public override string ToString() =>
            $"{Label}: mean {Mean:F5} sd {StdDev:F5} min {Min:F5} max {Max:F5} " +
            $"never {Never}/{Positions}";
    }

    private static Spread Measure(string label, OffsetMode mode, Func<int, bool> fires)
    {
        var rates = new List<double>();

        Rolls.With(mode, () =>
        {
            foreach (var (x, y) in Rolls.Positions())
            {
                var hits = 0;
                for (var tick = 0; tick < Ticks; tick++)
                    if (fires(RNG.Roll(x, y, tick)))
                        hits++;
                rates.Add((double)hits / Ticks);
            }
        });

        var mean = rates.Average();
        return new Spread
        {
            Label = label,
            Mean = mean,
            StdDev = Math.Sqrt(rates.Sum(r => (r - mean) * (r - mean)) / rates.Count),
            Min = rates.Min(),
            Max = rates.Max(),
            Never = rates.Count(r => r == 0),
            Positions = rates.Count,
            Rates = rates.ToArray(),
        };
    }

    /// <summary>
    /// <b>The headline.</b> A reaction with <c>Probability 240</c> fires when
    /// <c>Roll(..) % 240 == 0</c>, and 240 does not divide 256, so adding the tick is enough
    /// on its own.
    ///
    /// <para>In vanilla, roughly a third of all positions hold no value divisible by 240 at
    /// all. At those positions the reaction is not unlikely, it is impossible, and it stays
    /// impossible for the life of the world however long the player waits. That is the bug in
    /// its clearest form, and it is the one the plain tick offset closes completely.</para>
    /// </summary>
    [GameTest]
    public static void AReactionProbabilityIsNoLongerAPropertyOfThePosition()
    {
        var vanilla = Measure("vanilla", OffsetMode.Off, r => r % 240 == 0);
        var tick = Measure("+tick", OffsetMode.Tick, r => r % 240 == 0);
        var cycle = Measure("+tick+cycle", OffsetMode.TickAndCycle, r => r % 240 == 0);
        Log.Info($"Probability 240 over {Ticks} ticks -- {vanilla} | {tick} | {cycle}");

        // Vanilla, stated as an assertion so a game update that fixes this upstream is
        // reported rather than silently tolerated.
        if (vanilla.Never < vanilla.Positions / 5)
            throw new AssertionException(
                $"expected the unmodified game to have many positions where this reaction can " +
                $"never fire, found only {vanilla.Never} of {vanilla.Positions}. Either the RNG " +
                $"changed or this test is no longer measuring what it thinks. ({vanilla})");

        foreach (var fixedUp in new[] { tick, cycle })
        {
            if (fixedUp.Never != 0)
                throw new AssertionException(
                    $"{fixedUp.Label}: {fixedUp.Never} position(s) still never fire at all. ({fixedUp})");

            // A quarter of vanilla's spread. Measured at roughly a quarter for +tick and a
            // thirteenth for +tick+cycle, so the threshold has real room in it.
            if (fixedUp.StdDev > vanilla.StdDev / 2.5)
                throw new AssertionException(
                    $"{fixedUp.Label}: spread across positions is {fixedUp.StdDev:F5}, barely " +
                    $"below vanilla's {vanilla.StdDev:F5}. ({fixedUp})");
        }

        // The average has to stay put: the point is to move the odds around, not to change them.
        AssertMeanIsIntact(vanilla, tick, cycle, nominal: 1.0 / 240);
    }

    /// <summary>
    /// <b>The limitation, asserted rather than only documented.</b> <c>RNG.RollPct</c> is
    /// <c>Roll(..) &amp; 0x7F</c>, and 128 divides 256, so <c>tick % 128</c> is already decided
    /// by the <c>tick &amp; 255</c> that chose the value. The outcome is therefore still a
    /// function of <c>tick &amp; 255</c> alone, still 256 outcomes on a 256-tick cycle, and no
    /// amount of elapsed time adds a sample.
    ///
    /// <para>What adding the tick does do here is <b>relocate</b> the noise rather than reduce
    /// it: outcome <c>i</c> becomes <c>(volume[i] + i) &amp; 0x7F</c> instead of
    /// <c>volume[i] &amp; 0x7F</c>, which is a fresh draw of the same size because the shipped
    /// volume has no systematic bias for the offset to cancel. The decisive evidence is the
    /// correlation asserted below -- a position that was unlucky in vanilla is merely an
    /// ordinary position afterwards, while some other position has taken its place and is
    /// exactly as unlucky. <see cref="ADegeneratePositionIsRescuedByTheTickAlone"/> and
    /// <see cref="AConfinedPositionIsRescuedByTheTickAlone"/> are the cases where there <i>is</i>
    /// a systematic bias, and there the tick alone is the whole fix;
    /// <see cref="TheSpreadThatSurvivesIsExactlyTheNoiseOfTheSamplesAPositionGets"/> is why the
    /// game has none.</para>
    ///
    /// <para>This matters more than the arithmetic makes it sound: <c>RollPct</c> is the
    /// most-used roll in the game. The cycle term is what reaches it.</para>
    /// </summary>
    [GameTest]
    public static void APercentageCheckNeedsTheCycleTermAsWellAsTheTick()
    {
        const int Chance = 5;   // 5 in 128, the kind of number a growth or decay check uses
        Func<int, bool> fires = r => (r & 0x7F) < Chance;

        var vanilla = Measure("vanilla", OffsetMode.Off, fires);
        var tick = Measure("+tick", OffsetMode.Tick, fires);
        var cycle = Measure("+tick+cycle", OffsetMode.TickAndCycle, fires);
        Log.Info($"RollPct < {Chance} over {Ticks} ticks -- {vanilla} | {tick} | {cycle}");

        if (vanilla.StdDev < vanilla.Mean / 4)
            throw new AssertionException(
                $"expected the unmodified game to be badly spread here, found sd {vanilla.StdDev:F5} " +
                $"against mean {vanilla.Mean:F5}. ({vanilla})");

        // Adding the tick alone changes essentially nothing, and that is the finding. Asserted
        // from both sides: if a future change to Tick mode ever did fix this, this test should
        // fail and be rewritten rather than keep asserting a limitation that no longer exists.
        var ratio = tick.StdDev / vanilla.StdDev;
        if (ratio < 0.75 || ratio > 1.35)
            throw new AssertionException(
                $"+tick changed the spread of a power-of-two check by a factor of {ratio:F2}, " +
                $"which contradicts the reasoning this mod's default rests on. ({tick})");

        // The sharper form of the same finding, and the one that says what actually happened:
        // if +tick had reduced the bias, an unlucky position would still be somewhat unlucky and
        // the two rate vectors would be positively correlated. They are not correlated at all,
        // which means the bias was picked up and put down somewhere else. Measured at -0.005
        // over 4096 positions offline and around zero here.
        var correlation = Correlation(vanilla.Rates, tick.Rates);
        if (Math.Abs(correlation) > 0.35)
            throw new AssertionException(
                $"vanilla and +tick per-position rates correlate at {correlation:F3}. The whole " +
                "account of what +tick does to a power-of-two modulus -- reshuffle, not reduce " +
                "-- depends on these being independent.");
        Log.Info($"RollPct < {Chance}: vanilla vs +tick correlation {correlation:F4}, " +
                 $"vanilla vs +tick+cycle {Correlation(vanilla.Rates, cycle.Rates):F4}");

        if (cycle.StdDev > vanilla.StdDev / 4)
            throw new AssertionException(
                $"+tick+cycle left the spread at {cycle.StdDev:F5} against vanilla's " +
                $"{vanilla.StdDev:F5}, so the cycle term is not doing its job. ({cycle})");

        AssertMeanIsIntact(vanilla, tick, cycle, nominal: (double)Chance / 128);
    }

    /// <summary>
    /// The smallest power-of-two case, and the one most likely to be visible in play: the
    /// even-odd coin flip that decides which way a pixel goes. Vanilla gives some positions a
    /// persistent 57:43 lean.
    /// </summary>
    [GameTest]
    public static void ACoinFlipIsFairEverywhereOnlyWithTheCycleTerm()
    {
        Func<int, bool> fires = r => r % 2 == 0;

        var vanilla = Measure("vanilla", OffsetMode.Off, fires);
        var tick = Measure("+tick", OffsetMode.Tick, fires);
        var cycle = Measure("+tick+cycle", OffsetMode.TickAndCycle, fires);
        Log.Info($"coin flip over {Ticks} ticks -- {vanilla} | {tick} | {cycle}");

        if (vanilla.Max - vanilla.Min < 0.05)
            throw new AssertionException(
                $"expected the unmodified game's luckiest and unluckiest positions to differ by " +
                $"at least 5 points, found {(vanilla.Max - vanilla.Min) * 100:F1}. ({vanilla})");

        if (cycle.StdDev > vanilla.StdDev / 4)
            throw new AssertionException(
                $"+tick+cycle left the coin flip's spread at {cycle.StdDev:F5} against vanilla's " +
                $"{vanilla.StdDev:F5}. ({cycle})");

        if (Math.Abs(cycle.Mean - 0.5) > 0.005)
            throw new AssertionException($"+tick+cycle made the coin unfair on average. ({cycle})");

        AssertMeanIsIntact(vanilla, tick, cycle, nominal: 0.5);
    }

    /// <summary>
    /// <b>Where adding the tick alone fixes a power-of-two modulus completely.</b>
    ///
    /// <para>Take a position whose 256 volume entries are all the same number. Vanilla gives it
    /// one outcome, forever: a <c>RollPct</c> check there is not biased, it is decided. Adding
    /// the tick turns that single value into <c>constant + tick</c>, which sweeps every residue
    /// mod 128 in turn, and the position becomes exactly fair.</para>
    ///
    /// <para>That is the mechanism the tick offset really has for a power-of-two modulus: it
    /// decorrelates the value from the index that chose it. It rescues a position whose entries
    /// carry structure, and the all-equal position is the extreme of carrying structure. The
    /// reason it does not help in practice is measured in
    /// <see cref="APercentageCheckNeedsTheCycleTermAsWellAsTheTick"/>: the real volume is
    /// <c>new Random(12345)</c> output, already independent of the index, so there is no
    /// structure left to destroy and the offset trades one 256-sample draw for another.</para>
    ///
    /// <para>Tested against <c>TickOffset</c> directly rather than through the game, because the
    /// game's volume cannot be made degenerate and this is a claim about the arithmetic.</para>
    /// </summary>
    [GameTest]
    public static void ADegeneratePositionIsRescuedByTheTickAlone()
    {
        const int Window = 4096;               // 16 full cycles; the outcome period is 128
        const int Expected = Window / 128;

        foreach (var constant in new[] { 0, 1, 77, 1_234_567, int.MaxValue - 1 })
        {
            var vanilla = new int[128];
            var offset = new int[128];
            Rolls.With(OffsetMode.Tick, () =>
            {
                for (var tick = 0; tick < Window; tick++)
                {
                    vanilla[constant & 0x7F]++;
                    offset[TickOffset.Apply(constant, tick) & 0x7F]++;
                }
            });

            if (vanilla.Count(c => c != 0) != 1)
                throw new AssertionException(
                    "a position with 256 identical entries should have exactly one vanilla outcome");

            // Off by at most one: Wrap folds at int.MaxValue, which is 127 mod 128, so a window
            // that crosses the fold has its run split and one residue picked up an extra visit.
            for (var residue = 0; residue < 128; residue++)
                if (Math.Abs(offset[residue] - Expected) > 1)
                    throw new AssertionException(
                        $"entries all {constant}: residue {residue} came up {offset[residue]} " +
                        $"times in {Window} ticks, expected about {Expected}. The tick offset " +
                        "does not sweep a degenerate position evenly after all.");
        }
    }

    /// <summary>
    /// <b>The partial case: a position confined to half the range.</b>
    ///
    /// <para>Give a position the values 0 to 63, four times each, in some order. Every
    /// <c>RollPct</c> there lands in 0..63, so residues 64 to 127 are unreachable and every
    /// low-threshold check fires at exactly twice its intended rate. Not decided like the
    /// all-equal position of <see cref="ADegeneratePositionIsRescuedByTheTickAlone"/>, but
    /// badly and systematically wrong.</para>
    ///
    /// <para>Adding the tick fixes it, and the reason generalizes: over a 256-tick cycle the
    /// offset <c>i</c> visits every residue mod 128 exactly twice, so the outcome distribution
    /// is the position's own value distribution <i>convolved with the uniform distribution</i>.
    /// Convolving anything with uniform gives uniform. So the tick offset removes <b>any</b>
    /// systematic bias a position has, whatever shape it takes, as long as the values do not
    /// depend on the index that selects them.</para>
    ///
    /// <para>What it cannot remove is the noise of having only 256 samples, which is measured
    /// in <see cref="TheSpreadThatSurvivesIsExactlyTheNoiseOfTheSamplesAPositionGets"/> and is
    /// the whole of what the shipped volume has.</para>
    /// </summary>
    [GameTest]
    public static void AConfinedPositionIsRescuedByTheTickAlone()
    {
        const int Confined = 64;               // values 0..63 only
        const int Chance = 5;
        const int Cells = 8000;   // enough that the worst residue sits near 2%, well inside the 10% gate

        // One cell, in detail. The shuffle is deterministic and is this mod's own hash rather
        // than System.Random, so the case is fixed forever rather than tied to a runtime.
        var cell = BuildConfinedCell(seed: 1, Confined);
        var vanillaHigh = 0;
        var offsetHigh = 0;
        var vanillaHits = 0;
        var offsetHits = 0;

        Rolls.With(OffsetMode.Tick, () =>
        {
            for (var tick = 0; tick < 256; tick++)
            {
                var raw = cell[tick & 0xFF];
                var offset = TickOffset.Apply(raw, tick);
                if ((raw & 0x7F) >= Confined) vanillaHigh++;
                if ((offset & 0x7F) >= Confined) offsetHigh++;
                if ((raw & 0x7F) < Chance) vanillaHits++;
                if ((offset & 0x7F) < Chance) offsetHits++;
            }
        });

        if (vanillaHigh != 0)
            throw new AssertionException(
                "the confined cell was built wrong: it should never reach residue 64 or above");
        if (offsetHigh < 256 / 4)
            throw new AssertionException(
                $"with the tick added, the confined position reached residue 64+ only " +
                $"{offsetHigh} times in 256 ticks; about half was expected. The offset is not " +
                "unlocking the residues the position could never produce.");

        // Vanilla fires at exactly twice nominal: 5 of the 64 reachable values are below 5.
        var vanillaRate = (double)vanillaHits / 256;
        var offsetRate = (double)offsetHits / 256;
        const double Nominal = (double)Chance / 128;
        if (Math.Abs(vanillaRate - 2 * Nominal) > 1e-9)
            throw new AssertionException(
                $"expected the confined position to fire at exactly {2 * Nominal:F5}, got {vanillaRate:F5}");
        if (Math.Abs(offsetRate - Nominal) > Nominal * 0.6)
            throw new AssertionException(
                $"with the tick added it fired at {offsetRate:F5} against a nominal {Nominal:F5}. " +
                "One cell carries real sampling noise, but not this much.");

        // The systematic claim needs an ensemble: one 256-sample draw is too noisy to show that
        // the offset distribution is flat, but the average over many confined cells is not.
        var residue = new long[128];
        Rolls.With(OffsetMode.Tick, () =>
        {
            for (var c = 0; c < Cells; c++)
            {
                var cells = BuildConfinedCell(seed: c + 2, Confined);
                for (var tick = 0; tick < 256; tick++)
                    residue[TickOffset.Apply(cells[tick & 0xFF], tick) & 0x7F]++;
            }
        });

        var expected = (double)Cells * 256 / 128;
        var worst = residue.Select(c => Math.Abs(c - expected) / expected).Max();
        Log.Info($"confined cells: worst residue deviates {worst * 100:F2}% from uniform " +
                 $"over {Cells} cells (vanilla leaves half the residues at 100%)");
        if (worst > 0.10)
            throw new AssertionException(
                $"averaged over {Cells} confined positions, some residue is {worst * 100:F1}% " +
                "away from uniform. Adding the tick is supposed to convolve any index-independent " +
                "value distribution with a uniform one, which leaves nothing but uniform.");
    }

    /// <summary>
    /// <b>The precondition on all of that, shown to be necessary.</b>
    ///
    /// <para>Adding the tick flattens any bias because the offset <c>i</c> convolves the
    /// position's value distribution with a uniform one -- but convolution only argues that way
    /// if the two are <i>independent</i>. If a position's values are a function of the index
    /// that selects them, adding the index reinforces the structure instead of cancelling it.
    /// </para>
    ///
    /// <para>The case: <c>V[i] = i % 64</c>, the same 256 values as
    /// <see cref="AConfinedPositionIsRescuedByTheTickAlone"/> but laid out in index order rather
    /// than shuffled. Vanilla leaves 64 of the 128 residues unreachable. Adding the tick leaves
    /// <b>exactly 64 unreachable</b> -- a different 64, no fewer -- and turns a coin flip that
    /// was exactly fair into one that always lands the same way. Strictly worse on that measure,
    /// not merely unimproved.</para>
    ///
    /// <para>Nothing in the shipped volume looks like this; it is <c>new Random(12345)</c> output
    /// and independent of the index by construction. The test exists because the independence
    /// assumption is doing real work in this mod's account of itself, and an assumption that is
    /// never exercised is one nobody notices going stale. The cycle term needs no such
    /// assumption, which is asserted here too.</para>
    /// </summary>
    [GameTest]
    public static void AnIndexCorrelatedPositionIsNotRescuedByTheTickAlone()
    {
        const int Confined = 64;

        // The same multiset as the shuffled cell, in index order. That is the only difference,
        // and it is the whole point.
        var ordered = new int[256];
        for (var i = 0; i < ordered.Length; i++)
            ordered[i] = i % Confined;

        var vanilla = Tally(ordered, OffsetMode.Off, 256);
        var tick = Tally(ordered, OffsetMode.Tick, 256);

        if (vanilla.Dead != 64)
            throw new AssertionException(
                $"expected the confined cell to leave exactly 64 residues unreachable, got {vanilla.Dead}");
        if (Math.Abs(vanilla.EvenRate - 0.5) > 1e-9)
            throw new AssertionException(
                $"expected its coin flip to be exactly fair, got {vanilla.EvenRate:F4}");

        if (tick.Dead != vanilla.Dead)
            throw new AssertionException(
                $"adding the tick changed the number of unreachable residues from {vanilla.Dead} " +
                $"to {tick.Dead}. The claim under test is that an index-correlated bias survives " +
                "the offset intact, so if this now improves things the mod's account of its own " +
                "precondition needs rewriting, not relaxing.");
        if (Math.Abs(tick.EvenRate - 1.0) > 1e-9)
            throw new AssertionException(
                $"expected the offset to collapse this position's coin flip to always-even, got " +
                $"{tick.EvenRate:F4}. (V[i] = i % 64 gives (2r + 64q) mod 128, which is even for " +
                "every i.)");

        // The contrast: the same values, independent of the index, behave as the convolution
        // argument says. 15-ish residues left empty is ordinary 256-sample noise -- 128 bins and
        // 256 balls leaves about 128/e^2 = 17 empty however fairly they are thrown.
        var shuffled = Tally(BuildConfinedCell(seed: 1, Confined), OffsetMode.Tick, 256);
        if (shuffled.Dead > 30)
            throw new AssertionException(
                $"the shuffled cell left {shuffled.Dead} residues unreachable under +tick, which " +
                "is more than sampling noise explains; the contrast this test rests on is gone");
        if (Math.Abs(shuffled.EvenRate - 0.5) > 0.1)
            throw new AssertionException(
                $"the shuffled cell's coin flip came out at {shuffled.EvenRate:F4}");

        // And the cycle term carries no such precondition: it beats the correlated cell too,
        // because its offset depends on the cycle number rather than on the index.
        var cycle = Tally(ordered, OffsetMode.TickAndCycle, Ticks);
        if (cycle.Dead != 0)
            throw new AssertionException(
                $"+tick+cycle left {cycle.Dead} residues unreachable at an index-correlated " +
                $"position over {Ticks} ticks; it is supposed to need no independence assumption");
        if (Math.Abs(cycle.EvenRate - 0.5) > 0.05)
            throw new AssertionException(
                $"+tick+cycle left the correlated position's coin flip at {cycle.EvenRate:F4}");

        Log.Info($"index-correlated cell: vanilla {vanilla.Dead} dead residues / coin " +
                 $"{vanilla.EvenRate:F4} | +tick {tick.Dead} / {tick.EvenRate:F4} | " +
                 $"+tick+cycle {cycle.Dead} / {cycle.EvenRate:F4} | shuffled +tick " +
                 $"{shuffled.Dead} / {shuffled.EvenRate:F4}");
    }

    /// <summary>
    /// Runs one synthetic cell through <see cref="TickOffset"/> and reports how much of the
    /// residue space it reached and how its parity came out.
    /// </summary>
    private static (int Dead, double EvenRate) Tally(int[] cell, OffsetMode mode, int ticks)
    {
        var hits = new int[128];
        var even = 0;

        Rolls.With(mode, () =>
        {
            for (var tick = 0; tick < ticks; tick++)
            {
                var outcome = TickOffset.Apply(cell[tick & 0xFF], tick) & 0x7F;
                hits[outcome]++;
                if (outcome % 2 == 0)
                    even++;
            }
        });

        return (hits.Count(h => h == 0), (double)even / ticks);
    }

    /// <summary>
    /// 256 entries holding 0..<paramref name="confinedTo"/>-1 in equal numbers, shuffled by this
    /// mod's own hash so the case never moves.
    /// </summary>
    private static int[] BuildConfinedCell(int seed, int confinedTo)
    {
        var cell = new int[256];
        for (var i = 0; i < cell.Length; i++)
            cell[i] = i % confinedTo;

        for (var i = cell.Length - 1; i > 0; i--)
        {
            var j = (int)((uint)TickOffset.Mix(seed * 7919 + i) % (uint)(i + 1));
            (cell[i], cell[j]) = (cell[j], cell[i]);
        }
        return cell;
    }

    /// <summary>
    /// <b>Why the tick alone does nothing measurable on the volume the game ships.</b>
    ///
    /// <para>A position's rate can be off for exactly two reasons: its values are drawn from a
    /// distribution that is not uniform mod <c>m</c> (systematic), or it simply has too few
    /// samples for the rate to settle (noise). Adding the tick eliminates the first entirely and
    /// cannot touch the second, because it does not lengthen the 256-tick outcome period when
    /// <c>m</c> divides 256.</para>
    ///
    /// <para>This test measures which of the two the game actually has. If a position's spread
    /// is nothing but the noise of <c>N</c> samples, it must equal <c>sqrt(p(1-p)/N)</c>. It
    /// does, for N = 256, to within a couple of percent -- so the shipped volume carries no
    /// systematic bias for the tick offset to remove, and the cycle term earns its place by
    /// raising N to however long the world has been running rather than by removing a bias.</para>
    /// </summary>
    [GameTest]
    public static void TheSpreadThatSurvivesIsExactlyTheNoiseOfTheSamplesAPositionGets()
    {
        const int Chance = 5;
        Func<int, bool> fires = r => (r & 0x7F) < Chance;
        const double P = (double)Chance / 128;
        double Predicted(int samples) => Math.Sqrt(P * (1 - P) / samples);

        // vanilla and +tick: the outcome repeats every 256 ticks, so a position gets 256 samples
        // however long it waits. +tick+cycle: the outcome never repeats, so it gets one per tick.
        var cases = new[]
        {
            (Mode: OffsetMode.Off, Label: "vanilla", Samples: 256),
            (Mode: OffsetMode.Tick, Label: "+tick", Samples: 256),
            (Mode: OffsetMode.TickAndCycle, Label: "+tick+cycle", Samples: Ticks),
        };

        foreach (var (mode, label, samples) in cases)
        {
            var measured = Measure(label, mode, fires).StdDev;
            var ratio = measured / Predicted(samples);
            Log.Info($"{label}: spread {measured:F5}, noise of {samples} samples would be " +
                     $"{Predicted(samples):F5}, ratio {ratio:F2}");

            // Wide, because the spread of 144 positions is itself an estimate. Narrow enough to
            // catch the model being wrong by a factor, which is the only interesting failure.
            if (ratio < 0.6 || ratio > 1.5)
                throw new AssertionException(
                    $"{label}: per-position spread is {measured:F5}, but pure noise from " +
                    $"{samples} samples predicts {Predicted(samples):F5} (ratio {ratio:F2}). " +
                    "Either a position is not getting the sample count this mod's account says " +
                    "it gets, or there is a systematic bias here that the account does not allow.");
        }
    }

    /// <summary>
    /// <b>The cycle term does nothing inside a single cycle, and cannot.</b>
    ///
    /// <para>The offset is <c>tick + Mix(tick &gt;&gt; 8)</c>, and within one 256-tick cycle
    /// <c>tick &gt;&gt; 8</c> is constant, so the cycle term contributes a fixed rotation for the
    /// whole window. A position's 256 outcomes in that window are therefore
    /// <see cref="OffsetMode.Tick"/>'s outcomes, rigidly relabelled -- the same histogram, the
    /// same lumpiness, different residues wearing it.</para>
    ///
    /// <para>That is a floor, not a shortfall. Inside 256 ticks a position has exactly 256 values
    /// available to it, so no offset scheme of any kind can make it behave like more than 256
    /// samples. The cycle term's only available job is to make <i>successive</i> windows differ,
    /// so that counts accumulate instead of repeating. Which is why this mod does nothing
    /// measurable for a process that resolves in under about four seconds of game time, and
    /// everything for one a player waits on.</para>
    /// </summary>
    [GameTest]
    public static void WithinASingleCycleTheCycleTermChangesNothing()
    {
        const int Chance = 5;

        double SpreadOverOneCycle(OffsetMode mode, int startCycle)
        {
            var rates = new List<double>();
            Rolls.With(mode, () =>
            {
                foreach (var (x, y) in Rolls.Positions())
                {
                    var hits = 0;
                    for (var tick = startCycle * 256; tick < (startCycle + 1) * 256; tick++)
                        if ((RNG.Roll(x, y, tick) & 0x7F) < Chance)
                            hits++;
                    rates.Add(hits / 256.0);
                }
            });
            var mean = rates.Average();
            return Math.Sqrt(rates.Sum(v => (v - mean) * (v - mean)) / rates.Count);
        }

        var vanilla = SpreadOverOneCycle(OffsetMode.Off, 0);

        // Several cycles, because cycle 0 is the one where Mix(0) happens to contribute nothing
        // and would flatter the claim by being literally identical rather than merely as lumpy.
        foreach (var cycle in new[] { 0, 1, 7, 40 })
        {
            var oneCycle = SpreadOverOneCycle(OffsetMode.TickAndCycle, cycle);
            var ratio = oneCycle / vanilla;
            Log.Info($"cycle {cycle}: +tick+cycle spread over 256 ticks {oneCycle:F5} " +
                     $"against vanilla {vanilla:F5}, ratio {ratio:F2}");

            if (ratio < 0.6 || ratio > 1.6)
                throw new AssertionException(
                    $"over cycle {cycle} alone, +tick+cycle's spread was {oneCycle:F5} against " +
                    $"vanilla's {vanilla:F5} (ratio {ratio:F2}). A single cycle offers a position " +
                    "256 values however they are offset, so anything but parity here means the " +
                    "account of where the cycle term's benefit comes from is wrong.");
        }

        // And over many cycles it is emphatically not parity, so the test above is measuring a
        // real constraint rather than a mod that does nothing.
        var many = Measure("+tick+cycle", OffsetMode.TickAndCycle, r => (r & 0x7F) < Chance).StdDev;
        if (many > vanilla / 4)
            throw new AssertionException(
                $"over {Ticks} ticks the spread was {many:F5} against vanilla's {vanilla:F5}; " +
                "the cycle term is supposed to pay off across cycles even though it cannot " +
                "inside one");
    }

    /// <summary>
    /// Pearson correlation between two positions-indexed rate vectors. Near zero means the
    /// second measurement learned nothing from the first: the same positions are not the
    /// unlucky ones any more.
    /// </summary>
    private static double Correlation(double[] a, double[] b)
    {
        double meanA = a.Average(), meanB = b.Average();
        double covariance = 0, varianceA = 0, varianceB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            var da = a[i] - meanA;
            var db = b[i] - meanB;
            covariance += da * db;
            varianceA += da * da;
            varianceB += db * db;
        }
        return covariance / Math.Sqrt(varianceA * varianceB);
    }

    /// <summary>
    /// Redistributing luck must not create or destroy any. A mod that made every reaction
    /// twice as likely would pass every spread check above and be wrong.
    /// </summary>
    private static void AssertMeanIsIntact(Spread vanilla, Spread tick, Spread cycle, double nominal)
    {
        foreach (var spread in new[] { vanilla, tick, cycle })
            if (Math.Abs(spread.Mean - nominal) > nominal * 0.25)
                throw new AssertionException(
                    $"{spread.Label}: average rate {spread.Mean:F5} is more than a quarter away " +
                    $"from the nominal {nominal:F5}. ({spread})");
    }
}

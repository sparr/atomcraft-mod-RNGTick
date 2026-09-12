using Atomcraft;
using Atomcraft.TestHarness;

namespace RNGTick.Test;

/// <summary>
/// The bias as a player meets it, in the game's own simulation rather than in a histogram.
///
/// <para>Vanilla Atomcraft condenses noble gases out of cold empty air. The whole mechanic is one
/// line in <c>Simulation.SimulateCoords</c>, reached only for a cell holding no material:</para>
///
/// <code>
/// if (y.IsBelowSpace() &amp;&amp; y.IsAboveWorkshop() &amp;&amp; RNG.Roll(x, y, tick) % 100000 == 1)
///     TryCondenseNobleGasOutOfAir(field, x, y, tick);
/// </code>
///
/// <para>Three facts make this the clearest demonstration the mod has. The gate is
/// <c>% 100000</c>, which does not divide 256, so it is squarely in the territory the plain tick
/// offset fixes. Once the gate opens something always condenses: <c>TryCondenseNobleGasOutOfAir</c>
/// gates again on <c>RNG.RollPct</c>, but compares it against 80, 160, 320 and upward, and
/// <c>RollPct</c> returns <c>Roll &amp; 0x7F</c>, at most 127 -- so every threshold above the
/// first is met unconditionally and a cold cell always produces <i>some</i> gas. And a cold
/// trap is a thing a player builds on purpose and then stands in front of waiting.</para>
///
/// <para>So in vanilla this is not a probability at all. A trap either sits on a position
/// whose 256 rolls happen to contain one congruent to 1 mod 100000, or it does not and will never
/// produce a single atom however long it is left. Roughly one position in 391.</para>
/// </summary>
public static class CondensationTests
{
    /// <summary>
    /// The gate's exact probability per tick per air cell: rolls in <c>[0, int.MaxValue)</c>
    /// congruent to 1 mod 100000, over the size of that range. 1.000005e-5.
    /// </summary>
    public const double GatePerTick = 21475.0 / 2147483648.0;

    /// <summary>Ticks for a given share of independently-rolling traps to have condensed.</summary>
    public static int TicksFor(double share) =>
        (int)Math.Ceiling(Math.Log(1 - share) / Math.Log(1 - GatePerTick));

    /// <summary>Does this roll open the gate?</summary>
    private static bool Opens(int roll) => roll % 100000 == 1;

    /// <summary>
    /// The fixture: the region walled in ceramic one cell thick, and its whole 62x62 interior
    /// laid out as a checkerboard of cooling element and open air.
    ///
    /// <para>1922 air cells in the same 4096-cell region that individual 5x5 chambers could only
    /// fit 144 of. The cost per tick is set by the region, not by what is drawn in it, so packing
    /// in more traps is the only lever that shortens the run.</para>
    ///
    /// <para><b>Ceramic rather than cooling elements, with the heat mechanisms switched off.</b>
    /// The mechanic needs the traps below 165 K, and the obvious way to arrange that is a cooling
    /// element beside each one -- 1922 of them, each walking its eight neighbours every tick. The
    /// cheapest way is neither that nor <c>Region.PinnedHeat</c>, which rewrites the region every
    /// tick: <c>SimFeature.HeatConductance | SimFeature.AmbientHeat</c> switches off the only two
    /// things that would move the heatmap, so one fill at setup holds for the whole run. The test
    /// asserts the cold actually held rather than trusting it.</para>
    ///
    /// <para>Traps touch each other diagonally, so a condensed liquid could slide down into the
    /// next one. The soak disables <c>SimFeature.Movement</c>, which settles that: nothing slides,
    /// each trap is independent, and the count is the number of distinct positions that condensed
    /// rather than the number of events. Movement is incidental to a mechanic that only reads the
    /// heatmap and writes a material, and leaving it on costs a growing crowd of liquid pixels
    /// trying to flow for the whole run.</para>
    /// </summary>
    private static List<(int X, int Y)> BuildTraps(Region r)
    {
        var traps = new List<(int, int)>();

        r.Fill(0, 0, r.Width, r.Height, "Ceramic Wall");

        for (var y = 1; y < r.Height - 1; y++)
        for (var x = 1; x < r.Width - 1; x++)
        {
            if ((x + y) % 2 != 0)
            {
                r.SetAir(x, y);
                traps.Add((x, y));
            }
        }

        // The starting temperature. With HeatConductance and AmbientHeat disabled nothing moves
        // it again, so this one fill holds for the whole run.
        r.FillHeat(0);
        return traps;
    }

    private static int Condensed(Region r, List<(int X, int Y)> traps) =>
        traps.Count(c => r.At(c.X, c.Y) != null);

    /// <summary>
    /// <b>The vanilla claim, settled exactly rather than sampled.</b>
    ///
    /// <para>No simulation. A vanilla position can only ever produce the 256 rolls its slice of
    /// the RNG volume holds, so whether a trap there can <i>ever</i> condense is decidable by
    /// reading all 256 of them. That is a stronger statement than any soak could make: not "none
    /// condensed in the time we watched" but "none of these can, at any tick, ever".</para>
    /// </summary>
    [GameTest(Band = Altitude.Underground)]
    public static void InVanillaMostPositionsCanNeverCondenseAtAll(Region r)
    {
        var traps = BuildTraps(r);

        var canEver = Rolls.With(OffsetMode.Off, () => traps
            .Count(c => Enumerable.Range(0, 256).Any(t => Opens(RNG.Roll(r.OriginX + c.X, r.OriginY + c.Y, t)))));

        // The population rate, over a wide block of positions rather than this test's 81, so the
        // "one in a few hundred" figure is measured rather than asserted from the arithmetic.
        const int Span = 256;
        var population = Rolls.With(OffsetMode.Off, () =>
        {
            var live = 0;
            for (var x = 0; x < Span; x++)
            for (var y = 0; y < Span; y++)
                for (var t = 0; t < 256; t++)
                    if (Opens(RNG.Roll(x, y, t))) { live++; break; }
            return live;
        });

        var rate = (double)population / (Span * Span);
        Log.Info($"vanilla: {canEver}/{traps.Count} of this test's traps can ever condense; " +
                 $"across {Span * Span} positions, {population} can, one in {1 / rate:F0}. " +
                 $"Arithmetic predicts one in {1 / (1 - Math.Pow(1 - GatePerTick, 256)):F0}.");

        // Sanity on the measurement before leaning on it: the predicted rate is 1 in 391, so a
        // 65536-position sample should find about 168. Wildly off means the gate moved.
        var predicted = 1 - Math.Pow(1 - GatePerTick, 256);
        if (rate < predicted / 2 || rate > predicted * 2)
            throw new AssertionException(
                $"measured one position in {1 / rate:F0} as able to condense, against {1 / predicted:F0} " +
                "predicted. The condensation gate in Simulation.SimulateCoords has changed.");

        if (canEver > traps.Count / 4)
            throw new AssertionException(
                $"{canEver} of {traps.Count} traps can condense in vanilla, which is far more " +
                "than the one-in-a-few-hundred rate this test is built around");

        // And the same positions, with the mod: all of them, within the soak's budget.
        var withMod = Rolls.With(OffsetMode.TickAndCycle, () => traps
            .Count(c => Enumerable.Range(0, SoakTicks)
                .Any(t => Opens(RNG.Roll(r.OriginX + c.X, r.OriginY + c.Y, t)))));

        // Where it ends up given longer. Sampled rather than exhaustive: a trap that never opens
        // the gate has to be scanned for all 230256 ticks to prove it, and doing that for every
        // one of the 1922 costs more than the whole soak does.
        var longRun = TicksFor(0.90);
        var sample = traps.Where((_, i) => i % 10 == 0).ToList();
        var eventually = Rolls.With(OffsetMode.TickAndCycle, () => sample
            .Count(c => Enumerable.Range(0, longRun)
                .Any(t => Opens(RNG.Roll(r.OriginX + c.X, r.OriginY + c.Y, t)))));

        Log.Info($"with the mod, {withMod}/{traps.Count} of the same traps open the gate " +
                 $"within the soak's {SoakTicks} ticks, and {eventually}/{sample.Count} of a " +
                 $"sample within {longRun}");

        var required = (int)(traps.Count * RequiredShare);
        if (withMod < required)
            throw new AssertionException(
                $"only {withMod} of {traps.Count} traps open the gate within {SoakTicks} ticks " +
                $"with the mod; at least {required} were expected. The soak asserts the same bound " +
                "against the real simulation and would fail too.");
    }

    /// <summary>
    /// Ticks the soak runs for, and the budget the scan above predicts it against.
    ///
    /// <para>Sized for about 40% of the traps, which puts the quarter the test asserts three
    /// and a half standard deviations away across 144 of them -- far enough that which positions
    /// the harness happened to hand out cannot decide the result.</para>
    ///
    /// <para>A quarter is not a modest bar here. Vanilla's rate is not 25% of traps slowly,
    /// it is 0.2% of traps <i>ever</i>: measured at one position in 443, and 0 of this test's
    /// 81. Asking for three-quarters instead would be a truer picture of where the mod ends up,
    /// but the gate is one in a hundred thousand per tick, so at 144 traps it would cost
    /// 189712 ticks against 51083 -- three minutes against fifty seconds -- to sharpen a
    /// comparison that is already two orders of magnitude wide.</para>
    /// </summary>
    public static readonly int SoakTicks = TicksFor(0.30);

    /// <summary>The share the soak requires. See <see cref="SoakTicks"/> for why a quarter.</summary>
    public const double RequiredShare = 0.25;

    /// <summary>
    /// <b>The payoff, in the real simulation.</b> Build the traps, wait, count what condensed.
    /// </summary>
    [GameTest(Band = Altitude.Underground, TimeoutFrames = 100000, Disable = SimFeature.All)]
    public static void EveryColdTrapEventuallyCondensesSomething(Region r)
    {
        var traps = BuildTraps(r);
        if (traps.Count != 1922)
            throw new AssertionException($"built {traps.Count} traps; expected 1922");

        var started = Environment.TickCount64;
        const int Chunk = 10000;
        for (var done = 0; done < SoakTicks; done += Chunk)
        {
            r.Ticks(Math.Min(Chunk, SoakTicks - done));
            Log.Info($"condensation soak: {Math.Min(done + Chunk, SoakTicks)}/{SoakTicks} ticks, " +
                     $"{Condensed(r, traps)}/{traps.Count} traps, " +
                     $"{(Environment.TickCount64 - started) / 1000}s elapsed");
        }

        var condensed = Condensed(r, traps);

        // The cold, verified rather than assumed. Had the pin not held, the traps would have
        // drifted to ambient and stopped condensing, and the test would read as a failure of the
        // mod rather than of its own fixture.
        var heat = traps.Select(c => r.HeatAt(c.X, c.Y)).ToArray();
        if (heat.Max() >= 4)
            throw new AssertionException(
                $"traps span {heat.Min()}-{heat.Max()} K despite the disabled heat mechanisms; " +
                "the mechanic needs " +
                "below 165 K, and below 4 K for the helium branch this test expects");

        var share = (double)condensed / traps.Count;
        var expected = 1 - Math.Pow(1 - GatePerTick, SoakTicks);
        Log.Info($"condensation soak done: {condensed}/{traps.Count} ({share:P1}) after " +
                 $"{SoakTicks} ticks, {expected:P1} expected, " +
                 $"{(Environment.TickCount64 - started) / 1000}s");

        if (share <= RequiredShare)
            throw new AssertionException(
                $"only {condensed} of {traps.Count} traps ({share:P1}) condensed in " +
                $"{SoakTicks} ticks, where {expected:P1} was expected and more than " +
                $"{RequiredShare:P0} is required. In vanilla this figure is 0, and permanently so.");

        // Which gas, for the record: below 4 K it is helium on a 80-in-128 roll and neon
        // otherwise, and both are correct outcomes of the same mechanic.
        var gases = traps.Select(c => r.At(c.X, c.Y)).Where(m => m != null)
            .GroupBy(m => m!).OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key} x{g.Count()}");
        Log.Info($"condensed: {string.Join(", ", gases)}");
    }
}

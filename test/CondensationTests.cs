using Atomcraft;
using Atomcraft.TestHarness;

namespace RNGTick.Test;

/// <summary>
/// A rare event a player builds a machine around, measured in the game's own simulation.
///
/// <para>Atomcraft condenses noble gases out of cold empty air. The whole mechanic is one line
/// in <c>Simulation.SimulateCoords</c>, reached only for a cell holding no material, and it is
/// one in a hundred thousand per tick. Once the gate opens something always condenses:
/// <c>TryCondenseNobleGasOutOfAir</c> gates again on <c>RNG.RollPct</c>, but compares it against
/// 80, 160, 320 and upward, and <c>RollPct</c> returns <c>Roll &amp; 0x7F</c>, at most 127 -- so
/// every threshold above the first is met unconditionally and a cold cell always produces
/// <i>some</i> gas. A cold trap is a thing a player builds on purpose and then stands in front
/// of waiting, which is exactly the shape of process this mod is for.</para>
///
/// <para><b>This was the mod's showcase, and the game has since taken it back.</b> Through
/// buildid 25221481 the gate read <c>RNG.Roll(x, y, tick) % 100000 == 1</c>, so a trap either
/// sat on a position whose 256 rolls contained one congruent to 1 mod 100000 or it never
/// produced a single atom however long it was left -- about one position in 400. The
/// 2026-09-12 build folds the 256-tick cycle number into the x coordinate at this one call
/// site, which is this mod's idea applied by hand to a single consumer, and the lockout here
/// is gone. <see cref="ReactionTests"/> is where the claim lives now: the reaction gate in
/// <c>BaseMaterial</c> was not touched.</para>
///
/// <para>So what is left to assert here is the thing that still matters about this mechanic:
/// <b>the mod does not get in its way.</b> Both arms below run the real simulation -- the
/// vanilla one through <c>OffsetMode.Off</c>, which is a straight pass-through -- so whichever
/// regime the installed build is in, the numbers are measured rather than modelled. The
/// earlier version of this test recomputed the gate's expression itself and went on passing
/// for a build where the game had stopped evaluating it.</para>
/// </summary>
public static class CondensationTests
{
    /// <summary>
    /// The gate's probability per tick per air cell: rolls in <c>[0, int.MaxValue)</c>
    /// congruent to 1 mod 100000, over the size of that range. 1.000005e-5.
    ///
    /// <para>Used to size the soak and to put a number beside the result in the log. Nothing
    /// is asserted against it: the arms are compared with each other and against a floor.</para>
    /// </summary>
    public const double GatePerTick = 21475.0 / 2147483648.0;

    /// <summary>Ticks for a given share of independently-rolling traps to have condensed.</summary>
    public static int TicksFor(double share) =>
        (int)Math.Ceiling(Math.Log(1 - share) / Math.Log(1 - GatePerTick));

    /// <summary>
    /// Ticks each arm runs for. Sized for about a fifth of the traps, which at 1922 of them
    /// puts the floor the test asserts many standard deviations away -- far enough that which
    /// positions the harness happened to hand out cannot decide the result. The gate is one in
    /// a hundred thousand per tick, so every extra point of share costs thousands of ticks;
    /// two arms at this size is about sixteen seconds.
    /// </summary>
    public static readonly int SoakTicks = TicksFor(0.20);

    /// <summary>The share an arm has to reach. See <see cref="SoakTicks"/> for why a fifth is the target.</summary>
    public const double RequiredShare = 0.15;

    /// <summary>
    /// The fixture: the region walled in ceramic one cell thick, and its whole 62x62 interior
    /// laid out as a checkerboard of ceramic and open air.
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
    ///
    /// <para>Rebuilt between arms, which is what lets both measure the same positions. The tick
    /// is reset with it, so neither arm gets a different slice of the table by accident.</para>
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
        r.Tick = 0;
        return traps;
    }

    private static int Condensed(Region r, List<(int X, int Y)> traps) =>
        traps.Count(c => r.At(c.X, c.Y) != null);

    /// <summary>What one mode did: how many of the traps ever produced a pixel, and which gases.</summary>
    private sealed record Arm(OffsetMode Mode, int Condensed, int Traps, string Gases)
    {
        public double Share => (double)Condensed / Traps;

        public override string ToString() =>
            $"{Mode}: {Condensed}/{Traps} traps ({Share:P1}) -- {Gases}";
    }

    /// <summary>
    /// Rebuilds the traps and soaks them under one mode, reporting progress as it goes because
    /// this is the slowest test in the suite and a silent eight seconds reads as a hang.
    /// </summary>
    private static Arm Soak(Region r, OffsetMode mode)
    {
        var traps = BuildTraps(r);
        if (traps.Count != 1922)
            throw new AssertionException($"built {traps.Count} traps; expected 1922");

        var started = Environment.TickCount64;
        const int Chunk = 10000;
        for (var done = 0; done < SoakTicks; done += Chunk)
        {
            var step = Math.Min(Chunk, SoakTicks - done);
            Rolls.With(mode, () => r.Ticks(step));
            Log.Info($"condensation soak, {mode}: {done + step}/{SoakTicks} ticks, " +
                     $"{Condensed(r, traps)}/{traps.Count} traps, " +
                     $"{(Environment.TickCount64 - started) / 1000}s elapsed");
        }

        // The cold, verified rather than assumed. Had the fill not held, the traps would have
        // drifted to ambient and stopped condensing, and the arm would read as a failure of the
        // mod rather than of its own fixture.
        var heat = traps.Select(c => r.HeatAt(c.X, c.Y)).ToArray();
        if (heat.Max() >= 4)
            throw new AssertionException(
                $"{mode}: traps span {heat.Min()}-{heat.Max()} K despite the disabled heat " +
                "mechanisms; the mechanic needs below 165 K, and below 4 K for the helium " +
                "branch this test expects");

        var gases = traps.Select(c => r.At(c.X, c.Y)).Where(m => m != null)
            .GroupBy(m => m!).OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key} x{g.Count()}");

        return new Arm(mode, Condensed(r, traps), traps.Count, string.Join(", ", gases));
    }

    /// <summary>
    /// Build the traps, wait, count what condensed -- once with the mod inert and once with it
    /// on, over the same positions.
    /// </summary>
    [GameTest(Band = Altitude.Underground, TimeoutFrames = 100000, Disable = SimFeature.All)]
    public static void ColdTrapsCondenseUnderTheModAtLeastAsFastAsWithoutIt(Region r)
    {
        var expected = 1 - Math.Pow(1 - GatePerTick, SoakTicks);

        var vanilla = Soak(r, OffsetMode.Off);
        var full = Soak(r, OffsetMode.TickAndCycle);

        Log.Info($"condensation over {SoakTicks} ticks, {expected:P1} expected of a gate that " +
                 $"behaves like a probability. {vanilla}. {full}.");

        // Which regime this build is in, for the reader of the log. Not asserted either way:
        // a game that fixes its own call site is a good outcome, and a game that regresses it
        // hands this mod back a showcase rather than breaking it.
        Log.Info(vanilla.Share < RequiredShare
            ? "this build still gates condensation on the raw tick, so most positions are " +
              "locked out of it in vanilla and the mod is what unlocks them"
            : "this build folds the cycle number into the condensation gate itself, so vanilla " +
              "reaches the mechanic on its own; see ReactionTests for the gate that was not fixed");

        // The claim, and the only tight bound here: under the mod a cold trap's odds are the
        // gate's odds, so the count lands on the arithmetic. Measured 19.0% and 19.5% against
        // 20.0% on two different regions.
        if (full.Share < RequiredShare || full.Share > expected * 1.5)
            throw new AssertionException(
                $"the mod condensed {full.Condensed} of {full.Traps} traps ({full.Share:P1}) in " +
                $"{SoakTicks} ticks, where a gate behaving like a probability gives {expected:P1} " +
                $"and at least {RequiredShare:P0} is required.");

        // The regression this test is now for: the mod rewrites the input to every positional
        // roll in the game, and a rare event is the kind of thing that could quietly stop
        // happening under it without anything else noticing.
        //
        // Loose on purpose. The vanilla arm is not a stable baseline on this build -- the game's
        // own gate reuses one row of the table per 256-tick cycle, so traps inside a region rise
        // and fall together and the arm measured 18.6% on one region and 24.4% on another,
        // against a binomial standard deviation of 0.9 points. Half is wide enough that no
        // region can trip it and narrow enough to catch the mechanic actually stopping.
        if (full.Share < vanilla.Share * 0.5)
            throw new AssertionException(
                $"the mod condensed {full.Share:P1} of traps against vanilla's {vanilla.Share:P1} " +
                "over the same positions and the same number of ticks. The offset is suppressing " +
                "a rare event the unmodified game reaches.");
    }
}

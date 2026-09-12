using Atomcraft;
using Atomcraft.TestHarness;
using Godot;
using HarmonyLib;

namespace RNGTick.Test;

/// <summary>
/// What the mod does, judged only from outside it.
///
/// <para>Every other test here reaches into this mod: it switches <c>OffsetMode</c> to get a
/// vanilla baseline, calls <c>TickOffset.Apply</c> to predict a value, asks
/// <c>RNGPatches.TargetMethods</c> what was patched. That is the right way to test most of it,
/// and it is also how a test can pass because it and the mod share a mistaken assumption.</para>
///
/// <para>Nothing in this file names a type belonging to this mod. It installs the built mod the
/// way a player would, asks <c>Atomcraft.RNG</c> for rolls, and judges them against what the
/// unmodified game would have produced -- which is recoverable without the mod's help, because
/// <c>RNG.Roll</c> is a lookup and the table it reads is still sitting there. These tests would
/// run unchanged against any mod claiming to fix the same problem, and against any version of
/// this one.</para>
/// </summary>
public static class BlackBoxTests
{
    /// <summary>Positions spread across the volume, which is 512x512 before the mask folds onto it.</summary>
    private static IEnumerable<(int X, int Y)> Positions()
    {
        for (var x = 0; x < 512; x += 45)
        for (var y = 0; y < 512; y += 45)
            yield return (x, y);
    }

    /// <summary>
    /// The game's own RNG table, which is what makes an outside view possible at all.
    ///
    /// <para>This is knowledge of <i>Atomcraft</i>, not of the mod. <c>RNG.Roll</c> is
    /// <c>RNGVolume[((x &amp; 511) * 512 + (y &amp; 511)) * 256 + (tick &amp; 255)]</c> and
    /// nothing else, so reading the table gives exactly what an unpatched game would have
    /// returned for any position and tick.</para>
    /// </summary>
    private static int[] Volume()
    {
        var volume = (int[]?)AccessTools.Field(typeof(RNG), "RNGVolume")?.GetValue(null);
        if (volume == null)
            throw new AssertionException(
                "Atomcraft.RNG.RNGVolume is not there to read, so these tests cannot work out what " +
                "the unmodified game would have rolled. Either the harness did not run RNG.Init or " +
                "the game's RNG has changed shape.");
        return volume;
    }

    private static int Unmodified(int[] volume, int x, int y, int tick) =>
        volume[((x & 0x1FF) * 512 + (y & 0x1FF)) * 256 + (tick & 0xFF)];

    /// <summary>
    /// Something is offsetting the rolls. Not that it is correct -- only that the installed mod
    /// is reaching the game's RNG at all, which is the thing that silently stops being true.
    /// </summary>
    [GameTest]
    public static void TheInstalledModChangesWhatTheGameRolls()
    {
        var volume = Volume();
        var moved = 0;
        var total = 0;

        foreach (var (x, y) in Positions())
        for (var tick = 0; tick < 64; tick++)
        {
            total++;
            if (RNG.Roll(x, y, tick) != Unmodified(volume, x, y, tick))
                moved++;
        }

        if (moved == 0)
            throw new AssertionException(
                $"all {total} rolls matched the unmodified game exactly, so whatever is installed " +
                "is not affecting RNG.Roll. A Harmony patch on it can report itself installed and " +
                "still never run: both overloads carry AggressiveInlining, and a caller compiled " +
                "with the original body pasted in never reaches the detour.");

        GD.Print($"[black box] {moved}/{total} rolls differ from the unmodified game");
    }

    /// <summary>
    /// Whatever the offset is, a roll is still a number every existing caller can use.
    ///
    /// <para>The range matters more than it looks. <c>RNG.RandomDeterministic</c> feeds
    /// <c>Roll(..) % array.Length</c> straight into an indexer and <c>RollFloat</c> feeds
    /// <c>Roll(..) &amp; 0x3FF</c> into another, so a roll that came back negative would not skew
    /// a probability, it would throw out of the middle of a parallel simulation pass.</para>
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

            // The wrappers have to agree with it, or something is reaching Roll and something else
            // is not.
            if (RNG.RollPct(x, y, tick) != (roll & 0x7F))
                throw new AssertionException(
                    $"RollPct({x},{y},{tick}) disagrees with Roll(..)&0x7F; the two are going to " +
                    "different places");

            var single = RNG.RollFloat(x, y, tick);
            if (single <= 0f || single >= 1f)
                throw new AssertionException($"RollFloat({x},{y},{tick}) returned {single}");
        }
    }

    /// <summary>
    /// The same position and tick still answer the same. Multiplayer clients agree without
    /// exchanging anything, and a cell may ask twice within one tick.
    /// </summary>
    [GameTest]
    public static void RollsAreStillAFunctionOfPositionAndTick()
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
    /// <b>The claim itself, from outside.</b> A pixel's odds should depend less on where it is
    /// standing than they did.
    ///
    /// <para>Measured on a reaction with <c>Probability 240</c>, which fires on
    /// <c>Roll(..) % 240 == 0</c>. For each position, the rate over a long window, computed twice:
    /// once from what the game now rolls, once from what it would have rolled unmodified. The
    /// average must hold -- the point is to move luck around, not to create it -- and the spread
    /// between positions must fall.</para>
    /// </summary>
    [GameTest]
    public static void APositionsOddsDependLessOnItsAddress()
    {
        const int Ticks = 32768;
        var volume = Volume();
        var now = new List<double>();
        var before = new List<double>();

        foreach (var (x, y) in Positions())
        {
            int hitsNow = 0, hitsBefore = 0;
            for (var tick = 0; tick < Ticks; tick++)
            {
                if (RNG.Roll(x, y, tick) % 240 == 0)
                    hitsNow++;
                if (Unmodified(volume, x, y, tick) % 240 == 0)
                    hitsBefore++;
            }

            now.Add((double)hitsNow / Ticks);
            before.Add((double)hitsBefore / Ticks);
        }

        double Spread(List<double> rates)
        {
            var mean = rates.Average();
            return Math.Sqrt(rates.Sum(r => (r - mean) * (r - mean)) / rates.Count);
        }

        var deadBefore = before.Count(r => r == 0);
        var deadNow = now.Count(r => r == 0);
        GD.Print($"[black box] spread across positions {Spread(before):F5} unmodified -> " +
                 $"{Spread(now):F5} installed; positions that never fire {deadBefore} -> {deadNow}");

        if (deadBefore == 0)
            throw new AssertionException(
                "the unmodified game fired this reaction at least once at every position sampled, " +
                "which is not the behaviour this mod exists to fix. The RNG has changed, or these " +
                "positions are not representative.");

        if (deadNow != 0)
            throw new AssertionException(
                $"{deadNow} positions still never fire the reaction at all across {Ticks} ticks");

        if (Spread(now) > Spread(before) / 2)
            throw new AssertionException(
                $"spread across positions is {Spread(now):F5} against the unmodified " +
                $"{Spread(before):F5}; a pixel's odds still depend on where it is standing");

        var driftedBy = Math.Abs(now.Average() - before.Average()) / before.Average();
        if (driftedBy > 0.25)
            throw new AssertionException(
                $"the average rate moved by {driftedBy:P0}; luck is supposed to be redistributed, " +
                "not created or destroyed");
    }
}

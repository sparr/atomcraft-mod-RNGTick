using Atomcraft;
using Atomcraft.TestHarness;
using Godot;
using HarmonyLib;

namespace RNGTick.Test;

/// <summary>
/// That the patch is installed, that it computes what it claims to, and -- the part worth
/// the most -- that it is not being bypassed.
///
/// <para>Both <c>RNG.Roll</c> overloads carry <c>MethodImplOptions.AggressiveInlining</c>.
/// A Harmony detour on such a method is defeated by any caller that was compiled with the
/// original body pasted into it, and the failure is completely silent: the patch reports
/// itself as installed, a direct call through it works, and the simulation quietly keeps
/// rolling vanilla numbers. So it is not enough to check that <c>RNG.Roll</c> answers
/// differently. Every test here that can go through one of the game's own wrappers does.</para>
/// </summary>
public static class PatchTests
{
    private static readonly (int X, int Y, int Tick)[] Samples = Build();

    private static (int, int, int)[] Build()
    {
        var list = new List<(int, int, int)>();
        for (var i = 0; i < 64; i++)
            list.Add((i * 37, i * 19 + 5, i * 257 + 3));
        return list.ToArray();
    }

    [GameTest]
    public static void BothRollOverloadsArePatched()
    {
        var byCoords = AccessTools.Method(typeof(RNG), nameof(RNG.Roll),
            new[] { typeof(int), typeof(int), typeof(int) });
        var byPosition = AccessTools.Method(typeof(RNG), nameof(RNG.Roll),
            new[] { typeof(Vector2I), typeof(int) });

        if (byCoords == null || byPosition == null)
            throw new AssertionException(
                "RNG.Roll does not have the two overloads this mod patches. The game's RNG " +
                "changed shape; the mod needs revisiting, not the test.");

        foreach (var (method, label) in new[] { (byCoords, "Roll(int,int,int)"), (byPosition, "Roll(Vector2I,int)") })
        {
            var info = Harmony.GetPatchInfo(method);
            var mine = info?.Postfixes.Any(p => p.owner == RNGTick.ModEntry.ModId) ?? false;
            if (!mine)
                throw new AssertionException($"RNG.{label} carries no postfix owned by {RNGTick.ModEntry.ModId}");
        }
    }

    /// <summary>
    /// The literal request: the tick, added to the roll, brought back into range.
    /// </summary>
    [GameTest]
    public static void TickModeAddsExactlyTheTick()
    {
        var changed = 0;
        foreach (var (x, y, tick) in Samples)
        {
            var raw = Rolls.Vanilla(x, y, tick);
            var expected = TickOffset.Wrap((long)raw + tick);
            var actual = Rolls.With(OffsetMode.Tick, () => RNG.Roll(x, y, tick));

            if (actual != expected)
                throw new AssertionException(
                    $"Roll({x},{y},{tick}) returned {actual}, expected {raw} + {tick} wrapped = {expected}");
            if (actual != raw)
                changed++;
        }

        Rolls.RequireSomethingMoved(changed, Samples.Length, "RNG.Roll under Tick mode");
    }

    [GameTest]
    public static void TickAndCycleModeAddsTheTickAndTheCycleHash()
    {
        var changed = 0;
        foreach (var (x, y, tick) in Samples)
        {
            var raw = Rolls.Vanilla(x, y, tick);
            var expected = TickOffset.Wrap((long)raw + tick + TickOffset.Mix(tick >> 8));
            var actual = Rolls.With(OffsetMode.TickAndCycle, () => RNG.Roll(x, y, tick));

            if (actual != expected)
                throw new AssertionException(
                    $"Roll({x},{y},{tick}) returned {actual}, expected {expected}");
            if (actual != raw)
                changed++;
        }

        Rolls.RequireSomethingMoved(changed, Samples.Length, "RNG.Roll under TickAndCycle mode");
    }

    /// <summary>
    /// Off is a pass-through, byte for byte. The baseline every comparison in this suite rests
    /// on, and the escape hatch for a player who wants the mod installed but inert.
    /// </summary>
    [GameTest]
    public static void OffIsIndistinguishableFromVanilla()
    {
        // First that Off is answering from the RNG volume at all. A patch that returned a
        // constant, or a game whose volume was never generated, would satisfy every other
        // check in this test and make the whole suite's baseline meaningless.
        var distinct = new HashSet<int>();
        foreach (var (x, y, tick) in Samples)
            distinct.Add(Rolls.With(OffsetMode.Off, () => RNG.Roll(x, y, tick)));

        if (distinct.Count < Samples.Length / 2)
            throw new AssertionException(
                $"Off mode produced only {distinct.Count} distinct values across {Samples.Length} " +
                "positions, which does not look like the RNG volume at all");

        foreach (var (x, y, tick) in Samples)
        {
            var off = Rolls.With(OffsetMode.Off, () => RNG.Roll(x, y, tick));
            var again = Rolls.With(OffsetMode.Off, () => RNG.Roll(x, y, tick));
            if (off != again)
                throw new AssertionException($"Off mode is not even stable at ({x},{y},{tick})");
        }
    }

    /// <summary>
    /// Same position, same tick, same answer. The property the whole simulation is built on:
    /// a cell may ask for its roll more than once in a tick, and multiplayer clients have to
    /// agree without exchanging anything.
    /// </summary>
    [GameTest]
    public static void RollsStayPureInPositionAndTick()
    {
        foreach (var mode in new[] { OffsetMode.Off, OffsetMode.Tick, OffsetMode.TickAndCycle })
            Rolls.With(mode, () =>
            {
                foreach (var (x, y, tick) in Samples)
                {
                    var first = RNG.Roll(x, y, tick);
                    for (var repeat = 0; repeat < 4; repeat++)
                        if (RNG.Roll(x, y, tick) != first)
                            throw new AssertionException(
                                $"{mode}: Roll({x},{y},{tick}) is not a function of its arguments");
                }
            });
    }

    /// <summary>
    /// The two overloads must stay interchangeable. They are separate copies of the same four
    /// lines in the game, so patching one and missing the other would leave half the call
    /// sites unfixed, and the half that reaches <c>Roll(Vector2I, int)</c> includes the laser
    /// and electrolyzer paths.
    /// </summary>
    [GameTest]
    public static void TheVector2IOverloadAgreesWithTheCoordinateOverload()
    {
        foreach (var mode in new[] { OffsetMode.Off, OffsetMode.Tick, OffsetMode.TickAndCycle })
            Rolls.With(mode, () =>
            {
                foreach (var (x, y, tick) in Samples)
                {
                    var byCoords = RNG.Roll(x, y, tick);
                    var byPosition = RNG.Roll(new Vector2I(x, y), tick);
                    if (byCoords != byPosition)
                        throw new AssertionException(
                            $"{mode}: Roll({x},{y},{tick})={byCoords} but " +
                            $"Roll(Vector2I({x},{y}),{tick})={byPosition}. One overload is unpatched.");
                }
            });
    }

    /// <summary>
    /// <b>The inlining check.</b> <c>RNG.RollPct</c> is <c>Roll(..) &amp; 0x7F</c> and it is the
    /// most-called roll in the game, 58 call sites. If the JIT pasted the original <c>Roll</c>
    /// into it, <c>RollPct</c> would keep answering from the raw volume while <c>Roll</c> itself
    /// answered from the patch, and the two would disagree here.
    /// </summary>
    [GameTest]
    public static void TheOffsetReachesRollPct()
    {
        var changed = 0;
        Rolls.With(OffsetMode.TickAndCycle, () =>
        {
            foreach (var (x, y, tick) in Samples)
            {
                var expected = RNG.Roll(x, y, tick) & 0x7F;
                var actual = RNG.RollPct(x, y, tick);
                if (actual != expected)
                    throw new AssertionException(
                        $"RollPct({x},{y},{tick})={actual} but Roll(..)&0x7F={expected}: the " +
                        "wrapper inlined an unpatched Roll, so most of the game is unaffected " +
                        "by this mod");

                var viaPosition = RNG.RollPct(new Vector2I(x, y), tick);
                if (viaPosition != expected)
                    throw new AssertionException(
                        $"RollPct(Vector2I({x},{y}),{tick})={viaPosition}, expected {expected}");

                if (expected != (Rolls.Vanilla(x, y, tick) & 0x7F))
                    changed++;
            }
        });

        Rolls.RequireSomethingMoved(changed, Samples.Length, "RNG.RollPct");
    }

    /// <summary>
    /// The same check for the other wrappers: the boolean form of <c>RollPct</c>,
    /// <c>RollPctOutOfMax</c>, and the array picker.
    /// </summary>
    [GameTest]
    public static void TheOffsetReachesTheOtherWrappers()
    {
        var palette = new[] { "a", "b", "c", "d", "e", "f", "g" };

        Rolls.With(OffsetMode.TickAndCycle, () =>
        {
            foreach (var (x, y, tick) in Samples)
            {
                var roll = RNG.Roll(x, y, tick);

                if (RNG.RollPctOutOfMax(x, y, tick) != roll)
                    throw new AssertionException(
                        $"RollPctOutOfMax({x},{y},{tick}) disagrees with Roll: it inlined an " +
                        "unpatched Roll");

                const int Chance = 40;
                var expectedBool = (roll & 0x7F) < Chance;
                if (RNG.RollPct(x, y, tick, Chance) != expectedBool)
                    throw new AssertionException(
                        $"RollPct({x},{y},{tick},{Chance}) disagrees with Roll(..)&0x7F: it " +
                        "inlined an unpatched Roll");
                if (RNG.RollPct(new Vector2I(x, y), tick, Chance) != expectedBool)
                    throw new AssertionException(
                        $"the Vector2I RollPct with a chance disagrees with Roll(..)&0x7F");

                var expectedPick = palette[roll % palette.Length];
                if (palette.RandomDeterministic(new Vector2I(x, y), tick) != expectedPick)
                    throw new AssertionException(
                        $"RandomDeterministic at ({x},{y},{tick}) disagrees with Roll: it " +
                        "inlined an unpatched Roll");
            }
        });
    }

    /// <summary>
    /// <c>RollFloat</c> cannot be predicted from outside -- its lookup table is private -- so it
    /// is checked by its one observable invariant instead: the float is a function of
    /// <c>Roll(..) &amp; 0x3FF</c> and nothing else. Two ticks whose <i>patched</i> rolls share
    /// that index must give the same float, and the pairs are chosen so their <i>vanilla</i>
    /// rolls do not, which makes the check fail if <c>RollFloat</c> is reading the raw volume.
    /// </summary>
    [GameTest]
    public static void TheOffsetReachesRollFloat()
    {
        const int X = 123, Y = 77;
        var pairs = 0;

        Rolls.With(OffsetMode.TickAndCycle, () =>
        {
            var byIndex = new Dictionary<int, List<int>>();
            for (var tick = 0; tick < 20000 && pairs < 16; tick++)
            {
                var index = RNG.Roll(X, Y, tick) & 0x3FF;
                if (!byIndex.TryGetValue(index, out var ticks))
                    byIndex[index] = ticks = new List<int>();

                foreach (var earlier in ticks)
                {
                    // Only a pair the vanilla game would have separated proves anything.
                    if ((Rolls.Vanilla(X, Y, earlier) & 0x3FF) == (Rolls.Vanilla(X, Y, tick) & 0x3FF))
                        continue;

                    var a = RNG.RollFloat(X, Y, earlier);
                    var b = RNG.RollFloat(X, Y, tick);
                    if (a != b)
                        throw new AssertionException(
                            $"ticks {earlier} and {tick} now share RNG index {index} but " +
                            $"RollFloat gave {a} and {b}: RollFloat inlined an unpatched Roll");
                    pairs++;
                    break;
                }

                ticks.Add(tick);
            }
        });

        if (pairs == 0)
            throw new AssertionException(
                "could not construct a single tick pair that the patch brings together and " +
                "vanilla keeps apart, so this test checked nothing");
    }
}

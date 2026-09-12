using Atomcraft;
using Atomcraft.TestHarness;
using Godot;

namespace RNGTick.Test;

/// <summary>
/// That an offset roll is still a number every existing caller can use.
///
/// <para>This is where a mod like this one would do real damage rather than merely fail to
/// help. <c>RNG.RandomDeterministic</c> feeds <c>Roll(..) % array.Length</c> straight into an
/// indexer and <c>RNG.RollFloat</c> feeds <c>Roll(..) &amp; 0x3FF</c> into another, so a roll
/// that came back negative would not skew a probability, it would throw
/// <c>IndexOutOfRangeException</c> out of the middle of a parallel simulation pass. Adding an
/// <c>int</c> to an <c>int</c> is exactly the operation that produces one.</para>
/// </summary>
public static class RangeTests
{
    /// <summary>
    /// Ticks chosen to break a careless implementation: the boundary where
    /// <c>roll + tick</c> overflows <c>int</c>, and the largest tick there is.
    /// </summary>
    private static readonly int[] HostileTicks =
    {
        0, 1, 2, 255, 256, 257, 4095, 65535, 1_000_000,
        int.MaxValue / 2, int.MaxValue - 2, int.MaxValue - 1, int.MaxValue,
    };

    [GameTest]
    public static void EveryRollStaysInTheRangeRandomNextPromises()
    {
        foreach (var mode in new[] { OffsetMode.Off, OffsetMode.Tick, OffsetMode.TickAndCycle })
            Rolls.With(mode, () =>
            {
                foreach (var (x, y) in Rolls.Positions())
                foreach (var tick in HostileTicks)
                {
                    var roll = RNG.Roll(x, y, tick);
                    if (roll < 0 || roll >= TickOffset.Range)
                        throw new AssertionException(
                            $"{mode}: Roll({x},{y},{tick}) = {roll}, outside [0,{TickOffset.Range})");

                    var byPosition = RNG.Roll(new Vector2I(x, y), tick);
                    if (byPosition < 0 || byPosition >= TickOffset.Range)
                        throw new AssertionException(
                            $"{mode}: Roll(Vector2I({x},{y}),{tick}) = {byPosition}, out of range");
                }
            });
    }

    /// <summary>
    /// The wrap on its own, at the edges the roll sweep cannot reach directly.
    /// </summary>
    [GameTest]
    public static void WrapBringsAnySumBackIntoRange()
    {
        var sums = new List<long>
        {
            // Widened before the arithmetic: Range is int.MaxValue, so "Range + 1" written in
            // int is a compile-time overflow rather than the value this test wants.
            0, 1, -1, TickOffset.Range - 1, TickOffset.Range, (long)TickOffset.Range + 1,
            -(long)TickOffset.Range, (long)int.MaxValue + int.MaxValue, long.MaxValue, long.MinValue,
            (long)int.MinValue * 2,
        };
        for (var i = 0; i < 1000; i++)
            sums.Add((long)i * 4_294_967_291L - long.MaxValue / 3);

        foreach (var sum in sums)
        {
            var wrapped = TickOffset.Wrap(sum);
            if (wrapped < 0 || wrapped >= TickOffset.Range)
                throw new AssertionException($"Wrap({sum}) = {wrapped}, outside [0,{TickOffset.Range})");
        }
    }

    /// <summary>
    /// The indexing callers, driven for real rather than reasoned about. A negative roll
    /// reaches these as an exception, not as a wrong answer.
    /// </summary>
    [GameTest]
    public static void TheIndexingCallersNeverGoOutOfBounds()
    {
        var palette = new[] { "a", "b", "c", "d", "e", "f", "g" };
        var seen = new HashSet<string>();

        foreach (var mode in new[] { OffsetMode.Tick, OffsetMode.TickAndCycle })
            Rolls.With(mode, () =>
            {
                foreach (var (x, y) in Rolls.Positions())
                foreach (var tick in HostileTicks)
                {
                    // Throws IndexOutOfRangeException rather than failing an assertion if the
                    // roll went negative, which is the point: the harness reports the throw.
                    seen.Add(palette.RandomDeterministic(new Vector2I(x, y), tick));

                    var f = RNG.RollFloat(x, y, tick);
                    if (f <= 0f || f >= 1f)
                        throw new AssertionException(
                            $"{mode}: RollFloat({x},{y},{tick}) = {f}, outside (0,1)");

                    var ranged = RNG.RollFloatRange(new Vector2I(x, y), tick, -5f, 5f);
                    if (ranged < -5f || ranged > 5f)
                        throw new AssertionException(
                            $"{mode}: RollFloatRange({x},{y},{tick}) = {ranged}, outside [-5,5]");

                    var pct = RNG.RollPct(x, y, tick);
                    if (pct < 0 || pct > 127)
                        throw new AssertionException(
                            $"{mode}: RollPct({x},{y},{tick}) = {pct}, outside [0,127]");
                }
            });

        // If the picker only ever returned one entry the loop above would have proven nothing
        // about the modulo.
        if (seen.Count < palette.Length)
            throw new AssertionException(
                $"RandomDeterministic only ever returned {seen.Count} of {palette.Length} entries");
    }

    /// <summary>
    /// The uninitialized window is left alone. <c>RNG.Init</c> runs when a session starts, not
    /// from <c>Game._Ready</c>, so between launch and entering a world every roll is a constant
    /// zero and the offset must not invent numbers there.
    /// </summary>
    [GameTest]
    public static void NothingIsOffsetBeforeTheRngIsInitialized()
    {
        // The harness initializes the volume on its behalf, so the state under test has to be
        // staged rather than waited for. Restored in a finally: leaving it false would make
        // every later test roll zeroes.
        if (!RNG.IsInitialized)
            throw new AssertionException("the harness was expected to have initialized the RNG");

        RNG.IsInitialized = false;
        try
        {
            Rolls.With(OffsetMode.TickAndCycle, () =>
            {
                foreach (var tick in new[] { 0, 1, 12345, int.MaxValue })
                {
                    var roll = RNG.Roll(7, 9, tick);
                    var vanilla = Rolls.With(OffsetMode.Off, () => RNG.Roll(7, 9, tick));
                    if (roll != vanilla)
                        throw new AssertionException(
                            $"with the RNG marked uninitialized, Roll(7,9,{tick}) returned {roll} " +
                            $"rather than the game's own {vanilla}");
                }
            });
        }
        finally
        {
            RNG.IsInitialized = true;
        }
    }
}

using System.Runtime.CompilerServices;

namespace RNGTick;

/// <summary>
/// How much of the tick to fold into a roll.
/// </summary>
public enum OffsetMode
{
    /// <summary>
    /// Pass every roll through untouched. Vanilla behavior, bias and all.
    ///
    /// Here so the mod can be turned off without being uninstalled, and so a test can
    /// measure the unmodified game through the same call path it measures the fix through.
    /// </summary>
    Off,

    /// <summary>
    /// Add the tick, and nothing else.
    ///
    /// Removes any systematic bias a position has, whatever its shape, and lengthens the
    /// outcome period for every modulus that does not divide 256 -- which covers all of the
    /// game's reaction probabilities. What it cannot do is add samples where the modulus does
    /// divide 256, which is where the shipped volume's whole remaining spread lives. See
    /// <see cref="TickOffset"/> for the split, and <see cref="TickAndCycle"/> for the rest.
    /// </summary>
    Tick,

    /// <summary>
    /// Add the tick plus a hash of the 256-tick cycle the tick falls in. The default.
    ///
    /// A strict superset of <see cref="Tick"/>: the tick is still added, and the extra term
    /// is what reaches the power-of-two moduli the tick alone cannot.
    /// </summary>
    TickAndCycle,
}

/// <summary>
/// The arithmetic this mod exists for.
///
/// <para><b>The bias.</b> <c>RNG.Roll(x, y, tick)</c> is a lookup, not a generator:
/// <c>RNGVolume[(x &amp; 511, y &amp; 511, tick &amp; 255)]</c>, filled once at startup from
/// <c>new Random(12345)</c>. The tick index is masked to 8 bits, so a pixel that never moves
/// has exactly <b>256 rolls, forever</b>, cycling every 256 ticks. Whatever the caller then
/// does with the roll -- <c>% 240</c> for a reaction, <c>&amp; 0x7F</c> for a percentage
/// check -- it is reducing those same 256 numbers, and the residues among them are lumpy in
/// a way that is fixed for the lifetime of the world. A third of all positions contain no
/// value at all that is divisible by 240, so a reaction with <c>Probability 240</c> is not
/// unlikely there, it is impossible, and stays impossible no matter how long the player
/// waits.</para>
///
/// <para><b>Adding the tick.</b> Offsetting the roll by the tick breaks the cycle, because
/// the roll is now a function of two things with different periods: the lookup repeats every
/// 256 ticks and the offset does not. Over 65536 ticks the spread of that
/// <c>Probability 240</c> reaction across positions drops by a factor of four and the
/// positions where it could never happen disappear entirely. That is the whole fix for any
/// modulus that does not divide 256. The rest of this comment is about the ones that do.</para>
///
/// <para><b>Two separate defects, and the tick only reaches one.</b> A position's rate can be
/// wrong because its 256 values are drawn from a distribution that is not uniform mod
/// <c>m</c> (systematic), or simply because 256 samples is not many (noise).</para>
///
/// <para><b>Systematic bias: removed entirely.</b> Over a 256-tick cycle the offset <c>i</c>
/// visits every residue mod 128 exactly twice, so the outcome distribution is the position's own
/// value distribution convolved with the uniform distribution, and convolving anything with
/// uniform gives uniform. A position holding one value 256 times has a single outcome in vanilla
/// and sweeps every residue evenly once offset; a position confined to 0..63 makes residues
/// 64..127 unreachable and fires low checks at exactly twice their rate in vanilla, and comes
/// back to nominal once offset. This holds for any shape of bias.</para>
///
/// <para><b>The one precondition</b> is that the values do not depend on the index that selects
/// them, because convolution only argues that way for independent distributions. Where they do
/// depend on it, adding the index reinforces the structure instead of cancelling it:
/// <c>V[i] = i % 64</c> leaves 64 of the 128 residues unreachable in vanilla and <i>still</i>
/// leaves exactly 64 unreachable after the offset, while collapsing a coin flip that was exactly
/// fair into one that always lands the same way. Asserted, because an assumption the mod's own
/// account leans on should not go unexercised. The cycle term needs no such assumption.</para>
///
/// <para><b>Sampling noise: untouched.</b> If <c>m</c> divides 256 then <c>tick % m</c> is
/// already determined by <c>tick &amp; 255</c>, which is the lookup index, so the outcome stays
/// a function of <c>tick &amp; 255</c>: 256 outcomes on a 256-tick cycle, however long the world
/// runs. Elapsed time has nothing to amortize because it adds no samples.</para>
///
/// <para><b>The shipped volume has only the second.</b> Its per-position spread matches
/// <c>sqrt(p(1-p)/256)</c> to within a couple of percent, which is the signature of pure
/// 256-sample noise and leaves no room for a systematic component -- unsurprising, since the
/// volume is <c>new Random(12345)</c> output and so is already uniform and already independent
/// of the index. So on a power-of-two modulus the tick offset relocates the remaining noise
/// rather than reducing it: measured across 4096 positions, vanilla and tick-offset rates
/// correlate at -0.005 and the worst position is identical in both.</para>
///
/// <para>That still makes <see cref="OffsetMode.Tick"/> a guarantee worth holding -- no position
/// can ever be systematically biased, whatever ends up in the volume, and the game can load one
/// from disk via <c>RNG.LoadFromFile</c>. It just does not pay off on the table shipped today,
/// and <c>RNG.RollPct</c> is <c>roll &amp; 0x7F</c>, m = 128, the single most-used roll in the
/// game.</para>
///
/// <para><b>The cycle term.</b> <see cref="OffsetMode.TickAndCycle"/> adds a hash of
/// <c>tick &gt;&gt; 8</c> as well: the number of the 256-tick cycle, which is exactly the
/// information the lookup index throws away. The offset is then no longer a function of
/// <c>tick &amp; 255</c> for any modulus, so the outcome sequence stops repeating and a position
/// gains one sample per tick instead of being pinned at 256. That is a different mechanism from
/// the tick term: it attacks the noise rather than the systematic bias, and the spread falls as
/// <c>sqrt(256 / ticks)</c> -- measured 0.00094 after 32768 ticks against 0.01125 in vanilla,
/// where the model predicts 0.00107. The hash is the game's own, lifted from
/// <c>Simulation.PRNG</c>, so consecutive cycles get unrelated offsets rather than a slow
/// ramp.</para>
///
/// <para><b>It buys nothing inside a single cycle.</b> <c>tick &gt;&gt; 8</c> is constant across
/// a 256-tick window, so the cycle term contributes one fixed rotation there: the same histogram
/// as <see cref="OffsetMode.Tick"/>, relabelled. Measured over single cycles the spread is 0.98x
/// to 1.10x vanilla's. That is a floor and not a shortfall -- a position has 256 values available
/// to it in 256 ticks, so nothing can make it behave like more than 256 samples -- and it means
/// this mod does nothing for a process resolving in under about four seconds of game time and
/// everything for one a player waits on.</para>
///
/// <para><b>Still deterministic.</b> Every mode is a pure function of position and tick, which
/// is the property the whole simulation is built on: multiplayer clients agree, and a roll
/// asked for twice in one tick answers the same both times. What changes is only that a
/// position's luck no longer repeats every 256 ticks.</para>
/// </summary>
public static class TickOffset
{
    /// <summary>
    /// The half-open range a roll must land in: <c>[0, int.MaxValue)</c>.
    ///
    /// That is <c>Random.Next()</c>'s own range and therefore the range every caller was
    /// written against. Holding it is not cosmetic. <c>RNG.RandomDeterministic</c> uses
    /// <c>Roll(..) % array.Length</c> directly as an index and <c>RNG.RollFloat</c> uses
    /// <c>Roll(..) &amp; 0x3FF</c> the same way, so a roll that came back negative would not
    /// bias anything, it would throw <c>IndexOutOfRangeException</c> out of the middle of the
    /// simulation.
    /// </summary>
    public const int Range = int.MaxValue;

    /// <summary>
    /// Offsets one roll. Called for every roll the simulation makes, so it stays branch-light
    /// and allocation-free.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Apply(int roll, int tick)
    {
        return RNGTickConfig.Mode switch
        {
            OffsetMode.Off => roll,
            OffsetMode.Tick => Wrap((long)roll + tick),
            _ => Wrap((long)roll + tick + Mix(tick >> 8)),
        };
    }

    /// <summary>
    /// Brings a sum back into <see cref="Range"/>.
    ///
    /// The addition is done in <c>long</c> so that the wrap is the only thing that moves a
    /// value: done in <c>int</c>, a roll near <c>int.MaxValue</c> plus a large tick would
    /// overflow to a negative number first, and the modulo would then preserve the sign.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Wrap(long sum)
    {
        sum %= Range;
        if (sum < 0)
            sum += Range;
        return (int)sum;
    }

    /// <summary>
    /// The game's own integer hash, copied verbatim from the private <c>Simulation.PRNG</c>,
    /// which uses it to pick a rasterization order from the tick.
    ///
    /// Borrowed rather than invented so that the mod introduces no new notion of randomness
    /// into a simulation that already has one, and because it is a decent avalanche mix: a
    /// change in the low bit of the input changes roughly half the output bits, which is the
    /// property the cycle term needs.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Mix(int input)
    {
        unchecked
        {
            input = ((input >> 16) ^ input) * 73244475;
            input = ((input >> 16) ^ input) * 73244475;
            return (input >> 16) ^ input;
        }
    }
}

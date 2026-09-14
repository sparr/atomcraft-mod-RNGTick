using Atomcraft;
using Atomcraft.TestHarness;

namespace RNGTick.Test;

/// <summary>
/// <b>Is each defect this mod exists for still in the game?</b>
///
/// <para>Every test here asserts that the <i>unmodified</i> game is still broken in some
/// particular way. None of them says anything about whether this mod works: that is
/// <see cref="BiasTests"/>, <see cref="ReactionTests"/>, <see cref="PatchTests"/> and the
/// conformance suite, all of which set absolute bars and would pass unchanged against a game
/// that fixed itself tomorrow.</para>
///
/// <para><b>A failure here is good news.</b> It means the game no longer has the defect, and
/// the part of this mod that addressed it can be retired. Each failure message says which part.
/// That is a different question from correctness and it deserves a different answer than a red
/// suite, so this class is excluded from the default run -- <c>./run-tests.sh --retirement</c>
/// asks it deliberately.</para>
///
/// <para>The separation was forced by a real event. The 2026-09-12 build began filling the RNG
/// volume with a shuffled permutation of 0-255 per position, which made every power-of-two
/// consumer exactly uniform. Three tests that had been written as "vanilla is badly spread, the
/// mod fixes it" started failing on their first half, and a suite that is red because the game
/// improved cannot be read at a glance.</para>
/// </summary>
public static class RetirementTests
{
    /// <summary>
    /// <b>Retires: nothing. This is the one the mod still exists for.</b>
    ///
    /// <para>240 does not divide 256, so the balanced volume buys a reaction probability
    /// nothing: a position's 256 rolls either contain one congruent to 0 mod 240 or the reaction
    /// is impossible there for the life of the world. Measured on buildid 25276035: 44 of 144
    /// positions dead.</para>
    /// </summary>
    [GameTest]
    public static void VanillaStillLocksPositionsOutOfAReactionProbability()
    {
        var vanilla = BiasTests.Measure("vanilla", OffsetMode.Off, r => r % 240 == 0);
        Log.Info($"Probability 240 in vanilla over {BiasTests.Ticks} ticks -- {vanilla}");

        if (vanilla.Never < vanilla.Positions / 5)
            throw new AssertionException(
                $"only {vanilla.Never} of {vanilla.Positions} positions can never fire a " +
                $"1-in-240 reaction, where a fifth or more was expected. If this is a game fix " +
                $"rather than a broken measurement, the whole mod can be retired -- this is the " +
                $"defect it was built for. ({vanilla})");
    }

    /// <summary>
    /// <b>Retires: the in-simulation half of the same claim.</b>
    ///
    /// <para>The same question as above, put to the running simulation rather than to a
    /// histogram of rolls, over a shipped recipe. Vanilla's answer is not a low rate but a
    /// frozen one: every site that can react does so inside the first 256 ticks, and the count
    /// never moves again. See <see cref="ReactionTests"/> for the fixture.</para>
    /// </summary>
    [GameTest(Disable = SimFeature.All, TimeoutFrames = 30000)]
    public static void VanillaStillFreezesARareShippedReactionAfterOneCycle(Region r)
    {
        var recipe = ReactionTests.RequireRecipe();
        var ceiling = ReactionTests.Ceiling(recipe.Probability);
        var vanilla = ReactionTests.Measure(r, OffsetMode.Off, recipe.Temperature ?? 0);

        Log.Info($"{ReactionTests.Recipe} (1 in {recipe.Probability}) in vanilla -- {vanilla}. " +
                 $"{ReactionTests.Cycle} rolls per position cannot pass {ceiling:P1}.");

        if (vanilla.AfterAll != vanilla.AfterOneCycle)
            throw new AssertionException(
                $"vanilla gained {vanilla.AfterAll - vanilla.AfterOneCycle} reacting sites " +
                $"between tick {ReactionTests.Cycle} and tick {ReactionTests.Ticks}, so a " +
                $"position's luck no longer repeats every {ReactionTests.Cycle} ticks. If the " +
                "game's reaction gate stopped reading the raw tick, RNGTick has nothing left to " +
                "do for reactions and can be retired.");

        if (vanilla.Share > ceiling * 1.5 || vanilla.Share < ceiling * 0.5)
            throw new AssertionException(
                $"vanilla reacted at {vanilla.Share:P1} of sites against the {ceiling:P1} that " +
                $"{ReactionTests.Cycle} rolls of a 1-in-{recipe.Probability} gate predict. The " +
                "gate in BaseMaterial.IsReactionValid is not the one this suite was written " +
                "against; re-read it before trusting either verdict.");
    }

    /// <summary>
    /// <b>Retires: the cycle term's case for <c>RollPct</c>, the most-used roll in the game.</b>
    ///
    /// <para><b>Failing since buildid 25276035.</b> 128 divides 256, and the balanced volume
    /// makes <c>Roll &amp; 0x7F</c> visit every value exactly twice at every position, so the
    /// spread between positions is exactly 0.00000 and stays there however long a world runs.
    /// Vanilla is not merely unbiased here, it is better than any generator can be, and the mod
    /// trades that for an ordinary 256-sample draw. See the trade-off table in the README.</para>
    /// </summary>
    [GameTest]
    public static void VanillaIsStillBadlySpreadOnAPercentageCheck()
    {
        const int Chance = 5;
        var vanilla = BiasTests.Measure("vanilla", OffsetMode.Off, r => (r & 0x7F) < Chance);
        Log.Info($"RollPct < {Chance} in vanilla over {BiasTests.Ticks} ticks -- {vanilla}");

        if (vanilla.StdDev < vanilla.Mean / 4)
            throw new AssertionException(
                $"vanilla's spread on a power-of-two check is {vanilla.StdDev:F5} against a mean " +
                $"of {vanilla.Mean:F5}, which is not a bias a player would meet. The cycle term " +
                $"is what this mod adds for RollPct, and on this build it is not needed there. " +
                $"({vanilla})");
    }

    /// <summary>
    /// <b>Retires: the cycle term's case for an even-odd decision.</b>
    ///
    /// <para><b>Failing since buildid 25276035</b>, for the same reason as the percentage check:
    /// 2 divides 256, so a balanced permutation gives every position exactly 0.50000.</para>
    /// </summary>
    [GameTest]
    public static void VanillaStillLeansOnACoinFlip()
    {
        var vanilla = BiasTests.Measure("vanilla", OffsetMode.Off, r => r % 2 == 0);
        Log.Info($"coin flip in vanilla over {BiasTests.Ticks} ticks -- {vanilla}");

        if (vanilla.Max - vanilla.Min < 0.05)
            throw new AssertionException(
                $"vanilla's luckiest and unluckiest positions differ by " +
                $"{(vanilla.Max - vanilla.Min) * 100:F1} points, where at least 5 was expected. " +
                $"A coin flip no longer depends on where it is standing. ({vanilla})");
    }

    /// <summary>
    /// <b>Retires: nothing on its own, but it is the premise the rest of the account rests on.</b>
    ///
    /// <para><b>Failing since buildid 25276035.</b> This mod's account of the bug is that a
    /// position gets 256 samples and no more, so its spread is the noise of 256 samples. That was
    /// true while the volume was <c>new Random(12345)</c> output. A balanced volume is not
    /// sampling at all, so for a power-of-two modulus vanilla sits <i>below</i> the floor rather
    /// than on it, and the account holds only for moduli that do not divide 256.</para>
    /// </summary>
    [GameTest]
    public static void AVanillaPositionStillBehavesLikeJust256Samples()
    {
        const int Chance = 5;
        const double P = (double)Chance / 128;
        var floor = BiasTests.SamplingFloor(P, 256);

        var vanilla = BiasTests.Measure("vanilla", OffsetMode.Off, r => (r & 0x7F) < Chance);
        var ratio = vanilla.StdDev / floor;
        Log.Info($"vanilla: spread {vanilla.StdDev:F5}, noise of 256 samples would be " +
                 $"{floor:F5}, ratio {ratio:F2}");

        if (ratio < 0.6 || ratio > 1.5)
            throw new AssertionException(
                $"vanilla's per-position spread is {vanilla.StdDev:F5} against the {floor:F5} that " +
                $"256 samples predict (ratio {ratio:F2}). Below the floor means the volume is " +
                $"balanced for this modulus and the game is doing better than sampling; above it " +
                $"means a systematic bias this mod's account does not allow. Either way the " +
                $"account needs rewriting before the mod's claims about power-of-two consumers " +
                $"can be trusted. ({vanilla})");
    }

    /// <summary>
    /// <b>Retires: the argument for why the tick alone is not enough.</b>
    ///
    /// <para>If adding the tick had <i>reduced</i> a power-of-two bias rather than relocating it,
    /// an unlucky position would still be somewhat unlucky and the two rate vectors would
    /// correlate. Measured at about zero, which is what says the bias was picked up and put down
    /// somewhere else, and so why the mod ships the cycle term as its default.</para>
    ///
    /// <para><b>Failing since buildid 25276035</b>: correlation is undefined when every vanilla
    /// position has the same rate, which is exactly what a balanced volume produces.</para>
    /// </summary>
    [GameTest]
    public static void TheTickAloneOnlyRelocatesAPercentageCheckBias()
    {
        const int Chance = 5;
        Func<int, bool> fires = r => (r & 0x7F) < Chance;

        var vanilla = BiasTests.Measure("vanilla", OffsetMode.Off, fires);
        var tick = BiasTests.Measure("+tick", OffsetMode.Tick, fires);

        if (vanilla.StdDev == 0)
            throw new AssertionException(
                "every vanilla position has the same rate, so there is no bias for +tick to " +
                "relocate and no correlation to measure. The argument for the cycle term over " +
                $"the plain tick does not apply to this consumer any more. ({vanilla})");

        var correlation = BiasTests.Correlation(vanilla.Rates, tick.Rates);
        Log.Info($"RollPct < {Chance}: vanilla vs +tick correlation {correlation:F4}");

        if (Math.Abs(correlation) > 0.35)
            throw new AssertionException(
                $"vanilla and +tick per-position rates correlate at {correlation:F3}. The account " +
                "of what +tick does to a power-of-two modulus -- reshuffle, not reduce -- depends " +
                "on these being independent.");
    }
}

using Atomcraft;
using Atomcraft.TestHarness;

namespace RNGTick.Test;

/// <summary>
/// Shared machinery for asking the live game for rolls under a chosen mode.
///
/// <para>Everything here goes through <c>Atomcraft.RNG</c> itself rather than reimplementing
/// the lookup. A test that computed its own expected rolls would pass against a mod that
/// patched nothing, which is the one failure this suite exists to catch.</para>
/// </summary>
public static class Rolls
{
    /// <summary>
    /// Positions spread across the RNG volume, which is 512x512 before the mask folds the
    /// world onto it. The stride is coprime with 512 so the sample is not a sublattice of the
    /// volume's own structure.
    /// </summary>
    public const int Stride = 45;

    public static IEnumerable<(int X, int Y)> Positions()
    {
        for (var x = 0; x < 512; x += Stride)
        for (var y = 0; y < 512; y += Stride)
            yield return (x, y);
    }

    /// <summary>Runs <paramref name="body"/> with the mode forced, and puts it back afterwards.</summary>
    public static void With(OffsetMode mode, Action body)
    {
        var saved = RNGTickConfig.Mode;
        RNGTickConfig.Mode = mode;
        try
        {
            body();
        }
        finally
        {
            RNGTickConfig.Mode = saved;
        }
    }

    public static T With<T>(OffsetMode mode, Func<T> body)
    {
        var result = default(T)!;
        With(mode, () => { result = body(); });
        return result;
    }

    /// <summary>
    /// The roll the unmodified game would have produced. <see cref="OffsetMode.Off"/> is a
    /// straight pass-through, which is exactly what makes it usable as the baseline.
    /// </summary>
    public static int Vanilla(int x, int y, int tick) =>
        With(OffsetMode.Off, () => RNG.Roll(x, y, tick));

    /// <summary>
    /// Guards every test that compares patched output against vanilla: if the two are
    /// identical everywhere the test looked, the comparison proved nothing, and that is a
    /// failure of the test rather than a pass of the mod.
    /// </summary>
    public static void RequireSomethingMoved(int changed, int total, string what)
    {
        if (changed == 0)
            throw new AssertionException(
                $"{what}: not one of {total} samples differed from vanilla, so the patch is " +
                "not reaching this call path at all (or is not installed)");
    }
}

using Atomcraft.TestHarness;

namespace RNGTick.Test;

/// <summary>
/// That the game still works with every roll in it offset.
///
/// <para>These are deliberately ordinary scenes. The patch sits under the most-called method
/// in the simulation, so the risk it carries is not that a probability comes out slightly
/// wrong, it is that a roll lands somewhere no caller expected and takes a whole tick down
/// with it. <see cref="RangeTests"/> checks that by argument; this checks it by running the
/// simulation and seeing matter behave.</para>
/// </summary>
public static class SimulationTests
{
    [GameTest(Wall = "Granite")]
    public static void SandStillFallsToTheFloor(Region r)
    {
        r.Set(r.Width / 2, 2, "Sand");

        r.TicksUntil(() => r.At(r.Width / 2, r.Height - 2) == "Sand", 400,
            "sand falls to the floor of a walled region");
        r.AssertCount("Sand", 1);
    }

    /// <summary>
    /// A heap of grains collapsing is the busiest ordinary scene there is, and almost every
    /// decision in it -- which way a grain topples, whether it slides -- is a roll. Nothing
    /// may be created or destroyed along the way.
    ///
    /// <para>Conservation is the assertion that catches the failure mode this patch could
    /// plausibly cause. An offset roll that fell outside the range a caller expected would not
    /// show up as a slightly different pile; it would show up as a pixel written through a bad
    /// index, or a tick abandoned halfway through by an exception, and either way the count
    /// would move.</para>
    /// </summary>
    [GameTest(Wall = "Granite")]
    public static void GrainsAreConservedWhileAHeapCollapses(Region r)
    {
        const int Grains = 60;
        var floorY = r.Height - r.WallThickness - 1;

        // A tall, narrow, unstable column rather than a settled pile, so it spends the whole
        // test moving.
        for (var i = 0; i < Grains; i++)
            r.Set(r.Width / 2, floorY - 1 - i, "Sand");

        r.AssertCount("Sand", Grains);
        r.Ticks(400);
        r.AssertCount("Sand", Grains);

        if (r.CountInMargin("Sand") != 0)
            throw new AssertionException(
                $"{r.CountInMargin("Sand")} grain(s) escaped the walled region\n{r.Dump()}");

        // And it did collapse, so the run was not 400 ticks of nothing.
        var onTheFloor = 0;
        for (var x = 0; x < r.Width; x++)
            if (r.At(x, floorY) == "Sand")
                onTheFloor++;
        if (onTheFloor < 3)
            throw new AssertionException(
                $"the column never spread: {onTheFloor} grain(s) across the floor\n{r.Dump()}");
    }

    /// <summary>
    /// The same scene, twice, in the same process. The offset is a pure function of position
    /// and tick, so an identical scene must produce an identical world -- the property
    /// multiplayer desync detection rests on, and the one a stateful offset would break.
    /// </summary>
    [GameTest(Wall = "Granite")]
    public static void AnIdenticalSceneStillProducesAnIdenticalWorld(Region r)
    {
        var first = RunAndChecksum(r);
        r.Clear();
        r.BuildWalls("Granite");
        var second = RunAndChecksum(r);

        if (first != second)
            throw new AssertionException(
                $"the same scene checksummed {first} and then {second}: the offset is not a " +
                "pure function of position and tick");
    }

    private static int RunAndChecksum(Region r)
    {
        // Both runs must start from the same absolute tick. The offset is a function of the
        // tick's value, not of elapsed time, and Ticks() leaves the region wherever it
        // finished, so without this the second run would legitimately differ and the test
        // would be asserting that the mod does nothing.
        r.Tick = 1000;

        for (var x = 3; x < r.Width - 3; x += 5)
            r.Set(x, 3, "Sand");
        r.Fill(4, 8, 6, 3, "Water");
        r.Ticks(120);
        return r.Checksum();
    }
}

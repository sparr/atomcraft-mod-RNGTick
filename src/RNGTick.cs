using System.Reflection;
using Atomcraft;
using HarmonyLib;

namespace RNGTick;

/// <summary>
/// Offsets every deterministic roll by the current tick.
///
/// <para><b>The problem.</b> <c>RNG.Roll(x, y, tick)</c> is a lookup, not a generator:
/// <c>RNGVolume[(x &amp; 511, y &amp; 511, tick &amp; 255)]</c>, filled once at startup from
/// <c>new Random(12345)</c>. The tick index is masked to eight bits, so a pixel that never moves
/// has exactly <b>256 rolls, forever</b>, cycling every 256 ticks. Whatever the caller then does
/// with the roll -- <c>% 240</c> for a reaction, <c>&amp; 0x7F</c> for a percentage check -- it is
/// reducing those same 256 numbers, and the residues among them are lumpy in a way that is fixed
/// for the lifetime of the world. A third of all positions hold no value divisible by 240 at all,
/// so a reaction with <c>Probability 240</c> is not unlikely there, it is impossible.</para>
///
/// <para><b>The fix.</b> Offsetting by the tick gives the roll two periods instead of one: the
/// lookup repeats every 256 ticks and the offset does not. A hash of <c>tick &gt;&gt; 8</c> is
/// added as well, because if the modulus divides 256 then <c>tick % m</c> is already decided by
/// the <c>tick &amp; 255</c> that chose the value, and the tick alone would change nothing. That
/// is not a corner case: <c>RNG.RollPct</c> is <c>roll &amp; 0x7F</c> and is the most-used roll
/// in the game.</para>
///
/// <para><b>Still deterministic.</b> The offset is a pure function of position and tick, which is
/// the property the simulation is built on: multiplayer clients agree, and a roll asked for twice
/// in one tick answers the same both times.</para>
///
/// <para>Named for what the loader wants rather than for what it holds: <c>mod.json</c> names an
/// initClass, and that is this.</para>
/// </summary>
[HarmonyPatch]
public static class ModEntry
{
    /// <summary>
    /// Entry point, as named by <c>mod.json</c>.
    ///
    /// <para><b>This runs before the game has initialized anything.</b> The mod loader loads
    /// every mod during <c>SceneTree._initialize()</c>, and <c>Game._Ready</c> does not run until
    /// the following frame, so <c>Materials</c>, <c>Simulation</c>, and the RNG volume itself do
    /// not exist yet. Installing patches and returning is the only correct thing to do here --
    /// and it is also exactly what this mod needs, since the patch is in place before a single
    /// line of simulation code has been compiled.</para>
    /// </summary>
    public static void Initialize() =>
        new Harmony("RNGTick").PatchAll(Assembly.GetExecutingAssembly());

    /// <summary>
    /// Both <c>RNG.Roll</c> overloads. They do not share an implementation -- the second is a
    /// separate copy of the same four lines rather than a forward to the first -- so both need
    /// patching. Harmony binds a postfix's parameters by name, so one body covers both even
    /// though <c>tick</c> is the second parameter of one and the third of the other.
    /// </summary>
    public static IEnumerable<MethodBase> TargetMethods() =>
        AccessTools.GetDeclaredMethods(typeof(RNG)).Where(m => m.Name == nameof(RNG.Roll));

    [HarmonyPostfix]
    public static void AfterRoll(int tick, ref int __result)
    {
        // Before RNG.Init the volume is null and Roll returns a constant 0 for everything. That
        // is the game's own behavior in a state where nothing should be rolling yet, and an
        // offset applied to it would be inventing randomness rather than redistributing it.
        // RNG.Init runs when a session starts, not from Game._Ready, so this window lasts from
        // launch until the player enters a world.
        if (!RNG.IsInitialized)
            return;

        unchecked
        {
            // The game's own integer hash, lifted from the private Simulation.PRNG, over the
            // number of the 256-tick cycle. Borrowed rather than invented so the mod introduces
            // no new notion of randomness into a simulation that already has one.
            var cycle = tick >> 8;
            cycle = ((cycle >> 16) ^ cycle) * 73244475;
            cycle = ((cycle >> 16) ^ cycle) * 73244475;
            cycle = (cycle >> 16) ^ cycle;

            // Done in long so the wrap is the only thing that moves a value: in int, a roll near
            // int.MaxValue plus a large tick would overflow to a negative first, and the modulo
            // would preserve the sign. Callers use a roll as an array index -- RollFloat takes
            // `& 0x3FF`, RandomDeterministic takes `% array.Length` -- so a negative would not
            // skew a probability, it would throw out of the middle of the simulation.
            var sum = ((long)__result + tick + cycle) % int.MaxValue;
            __result = (int)(sum < 0 ? sum + int.MaxValue : sum);
        }
    }
}

using System.Reflection;
using Atomcraft;
using HarmonyLib;

namespace RNGTick;

/// <summary>
/// The whole mod: one postfix, applied to every <c>RNG.Roll</c> overload.
///
/// <para><b>Why only Roll.</b> Every deterministic, position-aware roll in the game funnels
/// through it. <c>RollPct</c>, <c>RollFloat</c>, <c>RollFloatRange</c>, <c>RollPctOutOfMax</c>,
/// and <c>RandomDeterministic</c> are all thin wrappers that call <c>Roll</c> and then mask or
/// divide, and the thirty remaining call sites in the simulation call it directly. Patching
/// <c>Roll</c> covers all of them, and patching the wrappers as well would apply the offset
/// twice.</para>
///
/// <para><b>Why the targets are discovered rather than listed.</b> There are two overloads
/// today, <c>Roll(int, int, int)</c> and <c>Roll(Vector2I, int)</c>, and they do not share an
/// implementation -- the second is a separate copy of the same four lines, not a forward to the
/// first -- so both genuinely need patching. Listing them would mean two <c>[HarmonyPatch]</c>
/// attributes over two identical method bodies, and an overload added by a game update would be
/// missed in silence: the mod would keep working, keep reporting itself installed, and quietly
/// leave whatever called the new overload unfixed. <see cref="TargetMethods"/> asks the assembly
/// instead, so the set is whatever the game actually has.</para>
///
/// <para>Harmony binds a postfix's parameters by name and type against each target separately,
/// so this works only while every overload keeps an <c>int tick</c> parameter and an <c>int</c>
/// return. One that did not would fail to bind at patch time, during <c>Initialize</c>, which is
/// the loudest moment available and long before any roll is made.</para>
///
/// <para><b>The tick-only RNG is deliberately left alone.</b>
/// <c>RollLowerThanChanceOutOf1024_TimeOnly</c> and <c>RollIntWithinRange</c> read a different
/// table indexed by the tick and nothing else. They have no position in them, so they cannot
/// have the per-position bias this mod exists to remove, and every cell asking on a given tick
/// is supposed to get the same answer.</para>
///
/// <para><b>Inlining.</b> Both overloads carry <c>MethodImplOptions.AggressiveInlining</c>,
/// which is normally the thing that defeats a Harmony patch: a caller compiled with the
/// original body inlined never reaches the detour. Two things make it work here. Harmony 2.4.2
/// sits on MonoMod, which clears the method's inlining flag as part of detouring it, and mods
/// are loaded during <c>SceneTree._initialize()</c>, a frame before <c>Game._Ready</c> and long
/// before any simulation code has been compiled. It is verified rather than assumed: the test
/// suite calls <c>RNG.RollPct</c> and checks the answer moved, which it only can if the
/// wrapper's call to <c>Roll</c> really went through the patch.</para>
/// </summary>
[HarmonyPatch]
public static class RNGPatches
{
    /// <summary>The name this mod patches, in one place rather than spelled into an attribute.</summary>
    public const string TargetName = nameof(RNG.Roll);

    public static IEnumerable<MethodBase> TargetMethods() =>
        AccessTools.GetDeclaredMethods(typeof(RNG)).Where(m => m.Name == TargetName);

    [HarmonyPostfix]
    public static void AfterRoll(int tick, ref int __result)
    {
        // Before RNG.Init the volume is null and Roll returns a constant 0 for everything. That
        // is the game's own behavior in a state where nothing should be rolling yet, and an
        // offset applied to it would be inventing randomness rather than redistributing it.
        // RNG.Init is called when a session starts, not from Game._Ready, so this window is real
        // and lasts from launch until the player enters a world.
        if (!RNG.IsInitialized)
            return;

        __result = TickOffset.Apply(__result, tick);
    }
}

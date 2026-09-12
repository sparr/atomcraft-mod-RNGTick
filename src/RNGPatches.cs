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

    /// <summary>
    /// How many overloads the mod was written against. Used only to notice that the number
    /// changed, never to require it: a game update that <i>adds</i> an overload is handled
    /// correctly by the discovery above, and failing to start over it would be a false alarm.
    /// One that removes or renames them is the real failure, and it is silent without a check.
    /// </summary>
    public const int KnownTargetCount = 2;

    public static IEnumerable<MethodBase> TargetMethods() =>
        AccessTools.GetDeclaredMethods(typeof(RNG)).Where(m => m.Name == TargetName);

    /// <summary>
    /// Checks the targets can still be bound, before Harmony tries and fails at it.
    ///
    /// <para>Harmony matches a postfix's parameters to the original's <b>by name</b>, which is
    /// what lets one postfix serve both overloads even though <c>tick</c> is the second parameter
    /// of one and the third of the other. It also means this mod depends on the game's parameter
    /// <i>name</i> -- a thing most developers would rename without a thought, and which a
    /// decompiler will happily show you long after it stopped being true.</para>
    ///
    /// <para>Without this, a rename surfaces as a Harmony exception naming a parameter, thrown
    /// from inside <c>PatchAll</c>, which takes the mod down and the test mod that depends on it
    /// with it -- so the visible symptom is a module that never loaded rather than anything
    /// pointing at the game. One sentence is worth more than that.</para>
    /// </summary>
    public static void RequireBindableTargets()
    {
        var targets = TargetMethods().ToList();

        if (targets.Count == 0)
            throw new InvalidOperationException(
                $"{nameof(RNG)}.{TargetName} does not exist. A game update renamed or removed it, " +
                "and this mod has nothing to patch.");

        foreach (var target in targets)
        {
            if (target is not MethodInfo method || method.ReturnType != typeof(int))
                throw new InvalidOperationException(
                    $"{nameof(RNG)}.{TargetName}({Signature(target)}) no longer returns int, so the " +
                    "postfix cannot take it as 'ref int __result'. A game update changed the RNG.");

            var tick = target.GetParameters().FirstOrDefault(p => p.Name == TickParameter);
            if (tick == null)
                throw new InvalidOperationException(
                    $"{nameof(RNG)}.{TargetName}({Signature(target)}) has no parameter named " +
                    $"'{TickParameter}'. Harmony binds postfix parameters by name, so a game update " +
                    "that renamed it leaves this mod unable to see the tick. Rename the postfix's " +
                    "parameter to match.");

            if (tick.ParameterType != typeof(int))
                throw new InvalidOperationException(
                    $"{nameof(RNG)}.{TargetName}({Signature(target)}) has '{TickParameter}' as " +
                    $"{tick.ParameterType.Name} rather than int, and Harmony matches on type as well " +
                    "as name.");
        }
    }

    /// <summary>The parameter this mod reads the tick from, by name. See RequireBindableTargets.</summary>
    public const string TickParameter = "tick";

    private static string Signature(MethodBase method) =>
        string.Join(", ", method.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));

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

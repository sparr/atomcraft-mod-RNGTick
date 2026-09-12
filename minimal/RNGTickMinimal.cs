// The whole of RNGTick, in one file and one method.
//
// NOT part of the build: it lives outside src/ so the real project's **/*.cs glob does not
// sweep it in. Two [HarmonyPatch] classes both postfixing RNG.Roll would apply the offset
// twice. It is here to show how little the mod actually is once the modes, the logging, the
// startup checks, and the commentary come off, and to be pasted into a fresh project by
// anyone who wants the behavior and nothing else.
//
// What it is: Atomcraft's RNG.Roll(x, y, tick) is a lookup into a fixed volume indexed by
// tick & 0xFF, so a pixel that never moves has 256 rolls, forever, and whatever a caller
// reduces them to is lumpy in a way that never averages out. This offsets every roll by the
// tick plus a hash of the 256-tick cycle, then wraps back into [0, int.MaxValue) -- the range
// Random.Next() promises, which matters because callers use a roll as an array index.
//
// Behavior matches the full mod's default (OffsetMode.TickAndCycle) exactly. The benchmark in
// test/RollBenchmarks.cs keeps a copy of the body below, asserts the two agree over a sweep,
// and times one against the other; if you edit this file, edit that copy with it.
//
// Ship it with a mod.json alongside, inside a folder named for the mod id:
//
//   { "id": "RNGTick", "name": "RNG Tick Offset", "version": "0.1.0", "author": "",
//     "modules": [ { "moduleId": "RNGTick/Main", "dll": "RNGTick.dll",
//                    "initClass": "RNGTick.RNGTickMinimal" } ] }

using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Atomcraft;
using HarmonyLib;

namespace RNGTick;

[HarmonyPatch]
public static class RNGTickMinimal
{
    /// <summary>Called by the mod loader, a frame before Game._Ready.</summary>
    public static void Initialize() =>
        new Harmony("RNGTick").PatchAll(Assembly.GetExecutingAssembly());

    /// <summary>
    /// Both Roll overloads. They do not share an implementation, and Harmony binds a postfix's
    /// parameters by name, so one body covers both even though tick is in a different position
    /// in each.
    /// </summary>
    public static IEnumerable<MethodBase> TargetMethods() =>
        AccessTools.GetDeclaredMethods(typeof(RNG)).Where(m => m.Name == nameof(RNG.Roll));

    [HarmonyPostfix]
    public static void AfterRoll(int tick, ref int __result)
    {
        // The one line here that is not the offset itself. Before RNG.Init -- which runs when a
        // session starts, not from Game._Ready -- the volume is null and Roll returns a constant
        // 0; offsetting that would invent randomness rather than redistribute it.
        if (!RNG.IsInitialized)
            return;

        unchecked
        {
            // The game's own integer hash, from the private Simulation.PRNG, over the 256-tick
            // cycle number. This is the term that reaches moduli dividing 256, such as the
            // & 0x7F behind RNG.RollPct: for those, tick % m is already decided by the
            // tick & 0xFF that chose the value, so the tick alone cannot lengthen the cycle.
            var cycle = tick >> 8;
            cycle = ((cycle >> 16) ^ cycle) * 73244475;
            cycle = ((cycle >> 16) ^ cycle) * 73244475;
            cycle = (cycle >> 16) ^ cycle;

            // In long, so a roll near int.MaxValue plus a large tick wraps rather than
            // overflowing to a negative that the modulo would then keep the sign of.
            var sum = ((long)__result + tick + cycle) % int.MaxValue;
            __result = (int)(sum < 0 ? sum + int.MaxValue : sum);
        }
    }
}

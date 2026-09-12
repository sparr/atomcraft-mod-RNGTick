using System.Reflection;
using HarmonyLib;

namespace RNGTick;

/// <summary>
/// Entry point, as named by <c>mod.json</c>.
///
/// <para><b>Initialize runs before the game has initialized anything.</b> The mod loader
/// loads every mod during <c>SceneTree._initialize()</c>, and the game's own
/// <c>Game._Ready</c> does not run until the following frame. So <c>Materials</c>,
/// <c>Simulation</c>, and the RNG volume itself do not exist yet, and the only correct thing
/// to do here is install patches and return. That ordering is also exactly what this mod
/// needs: the patches are in place before a single line of simulation code has been
/// compiled, which is what keeps an aggressively-inlined method patchable.</para>
/// </summary>
public static class ModEntry
{
    public const string ModId = "RNGTick";

    private static Harmony? _harmony;

    public static void Initialize()
    {
        // Before PatchAll rather than after: this is the check that turns a game update into a
        // sentence, and PatchAll is what would otherwise throw first and less helpfully.
        RNGPatches.RequireBindableTargets();

        _harmony = new Harmony(ModId);
        _harmony.PatchAll(Assembly.GetExecutingAssembly());

        // A patch that failed to bind is this mod's whole failure mode, and it is a silent one:
        // the game runs, nothing throws, and the rolls are simply never offset. Say so at startup
        // rather than leaving it to be noticed as "the mod did not seem to do anything".
        //
        // The targets are discovered, not listed, so the count is a floor rather than an
        // equality. Finding more overloads than the mod was written against means a game update
        // added one and it has been patched too, which is the intended behavior and not worth
        // refusing to start over; finding fewer means Roll was renamed or removed and nothing is
        // being offset at all.
        var patched = _harmony.GetPatchedMethods().Count();
        if (patched < RNGPatches.KnownTargetCount)
            Log.Error($"patched {patched} method(s), expected at least {RNGPatches.KnownTargetCount}. " +
                      $"{RNGPatches.TargetName} may have been renamed or removed by a game update; " +
                      "rolls are NOT being offset.");
        else if (patched > RNGPatches.KnownTargetCount)
            Log.Info($"initialized, mode {RNGTickConfig.Mode}, {patched} method(s) patched " +
                     $"({RNGPatches.KnownTargetCount} expected, so the game has gained an overload " +
                     "and it is covered)");
        else
            Log.Info($"initialized, mode {RNGTickConfig.Mode}, {patched} method(s) patched");
    }
}

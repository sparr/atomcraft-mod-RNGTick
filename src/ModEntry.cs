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
        _harmony = new Harmony(ModId);
        _harmony.PatchAll(Assembly.GetExecutingAssembly());

        Log.Info($"initialized, mode {RNGTickConfig.Mode}, " +
                 $"{_harmony.GetPatchedMethods().Count()} method(s) patched");
    }
}

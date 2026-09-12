using Atomcraft.TestHarness;

namespace RNGTick.Test;

/// <summary>
/// Entry point for the test mod.
///
/// A peer of <c>RNGTick</c> rather than a module of it, because the mod loader treats a
/// missing dependency as an error: a test module shipped inside the mod's own zip would show
/// a red entry in the loader report for every player who did not also install the harness.
/// </summary>
public static class ModEntry
{
    public const string ModId = "RNGTick.Test";

    /// <summary>The harness this mod is written against. Its 0.x API changes between minors.</summary>
    public const string HarnessVersion = "0.3";

    public static void Initialize()
    {
        Harness.RequireVersion(HarnessVersion);

        // The mode is the only state this mod has, and several tests change it to compare
        // vanilla against patched. Registering it means a test that changes it and then throws
        // cannot quietly change the meaning of every test after it. No checksum: it is a
        // setting a test chooses, not something the simulation writes to.
        StateRegistry.Register(new StateSpec
        {
            Name = "rngtick.config",
            OnReset = RNGTickConfig.Reset,
            OnDescribe = () => $"mode {RNGTickConfig.Mode}",
        });
    }
}

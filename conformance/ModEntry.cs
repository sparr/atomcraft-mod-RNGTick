using Atomcraft.TestHarness;

namespace RNGConformance;

/// <summary>
/// Entry point.
///
/// <para>Depends on the harness and on nothing else. In particular it does not depend on
/// RNGTick, or name it, or reference its assembly: the point of this mod is that it can be
/// installed alongside any candidate fix, or alongside none, and reach the same verdict either
/// way.</para>
/// </summary>
public static class ModEntry
{
    public const string ModId = "RNGConformance";

    /// <summary>The harness this is written against. Its 0.x API changes between minors.</summary>
    public const string HarnessVersion = "0.4";

    public static void Initialize() => Harness.RequireVersion(HarnessVersion);
}

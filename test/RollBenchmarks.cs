using System.Runtime.CompilerServices;
using System.Threading;
using System.Reflection;
using System.Diagnostics;
using Atomcraft;
using Atomcraft.TestHarness;
using HarmonyLib;

namespace RNGTick.Test;

/// <summary>
/// What the postfix costs per roll, measured rather than asserted.
///
/// <para>The mod's overhead has two parts and they need separating. <b>The detour</b>: a patched
/// method is entered through a Harmony wrapper instead of directly. <b>The lost inlining</b>:
/// <c>RNG.Roll</c> is four lines marked <c>AggressiveInlining</c>, and MonoMod clears that flag as
/// part of detouring it, so every call site that would have had the array lookup pasted in now
/// makes a real call. The second is invisible to a benchmark that only toggles the mode, because
/// the inlining is already gone by then.</para>
///
/// <para>So three measurements, over the same access pattern -- a 64x64 sweep per tick, which is
/// what a region tick actually does to the volume:</para>
///
/// <list type="bullet">
/// <item>the bare array lookup, standing in for what the JIT emits when <c>Roll</c> is inlined;</item>
/// <item><c>RNG.Roll</c> with the postfix removed at runtime, so a real call but no detour;</item>
/// <item><c>RNG.Roll</c> as the mod ships it, in each mode.</item>
/// </list>
///
/// <para>This asserts almost nothing. A timing threshold on a shared machine is a flaky test, and
/// the number worth having is the number itself, in the log. The one assertion is a sanity bound
/// wide enough that only a real regression trips it.</para>
/// </summary>
public static class RollBenchmarks
{
    /// <summary>Sweeps of a 64x64 block. 4096 rolls each, so 2000 sweeps is 8.2M rolls a rep.</summary>
    private const int Sweeps = 2000;

    private const int Reps = 5;

    /// <summary>Spread over position and tick, including ticks past one 256-cycle.</summary>
    private static readonly (int X, int Y, int Tick)[] Samples =
        Enumerable.Range(0, 512).Select(i => (i * 37, i * 19 + 5, i * 257 + 3)).ToArray();

    private static long SweepViaRoll()
    {
        long sum = 0;
        for (var t = 0; t < Sweeps; t++)
        for (var y = 0; y < 64; y++)
        for (var x = 0; x < 64; x++)
            sum += RNG.Roll(x, y, t);
        return sum;
    }

    /// <summary>
    /// The same work with the lookup written out, which is what a caller with <c>Roll</c> inlined
    /// into it ends up executing. Masks kept so the arithmetic matches.
    /// </summary>
    private static long SweepViaArray(int[] volume)
    {
        long sum = 0;
        for (var t = 0; t < Sweeps; t++)
        for (var y = 0; y < 64; y++)
        for (var x = 0; x < 64; x++)
            sum += volume[((x & 0x1FF) * 512 + (y & 0x1FF)) * 256 + (t & 0xFF)];
        return sum;
    }

    /// <summary>
    /// The wrap on its own, both ways, with everything else held identical.
    ///
    /// <para><c>TickOffset.Wrap</c> reduces modulo <c>int.MaxValue</c>, which is a 64-bit integer
    /// division -- tens of cycles, and not something the JIT can turn into a multiply because the
    /// divisor is not a power of two. Masking to 31 bits would give <c>[0, int.MaxValue]</c>
    /// instead: one value wider than <c>Random.Next()</c>'s range, and a power-of-two reduction
    /// with no modulo bias at all. This measures what that swap would be worth.</para>
    /// </summary>
    private static long SweepWrap(bool mask)
    {
        long sum = 0;
        unchecked
        {
            for (var t = 0; t < Sweeps; t++)
            for (var i = 0; i < 4096; i++)
            {
                var roll = i * 1103515245 + t;
                var raw = (long)roll + t + TickOffset.Mix(t >> 8);
                if (mask)
                {
                    sum += (int)(raw & 0x7FFFFFFF);
                }
                else
                {
                    var wrapped = raw % int.MaxValue;
                    if (wrapped < 0)
                        wrapped += int.MaxValue;
                    sum += (int)wrapped;
                }
            }
        }
        return sum;
    }

    /// <summary>Best of several runs, after a warm-up. The minimum is the least noisy estimator here.</summary>
    private static double NanosPerRoll(Func<long> work)
    {
        work();                                     // JIT and warm the volume into cache

        var best = double.MaxValue;
        for (var rep = 0; rep < Reps; rep++)
        {
            var watch = Stopwatch.StartNew();
            var sum = work();
            watch.Stop();
            if (sum == long.MinValue)               // never true; keeps the loop from being elided
                throw new AssertionException("unreachable");
            best = Math.Min(best, watch.Elapsed.TotalMilliseconds);
        }

        return best * 1_000_000.0 / (Sweeps * 4096.0);
    }

    [GameTest]
    public static void WhatThePostfixCostsPerRoll()
    {
        var volume = (int[]?)AccessTools.Field(typeof(RNG), "RNGVolume").GetValue(null);
        if (volume == null)
            throw new AssertionException("RNG.RNGVolume is null; the harness should have run RNG.Init");

        var inlined = NanosPerRoll(() => SweepViaArray(volume));

        var off = Rolls.With(OffsetMode.Off, () => NanosPerRoll(SweepViaRoll));
        var tick = Rolls.With(OffsetMode.Tick, () => NanosPerRoll(SweepViaRoll));
        var cycle = Rolls.With(OffsetMode.TickAndCycle, () => NanosPerRoll(SweepViaRoll));

        // The postfix taken off and put back. Note this does not restore inlining: MonoMod cleared
        // the flag when it first detoured the method and unpatching does not set it again, so what
        // this measures is a real call with no detour, not the vanilla game. That is the point --
        // it isolates the detour from the inlining, and `inlined` above covers the other half.
        var unpatched = WithoutThePostfix(() => NanosPerRoll(SweepViaRoll));

        Log.Info($"RNG.Roll, ns per roll over a 64x64 sweep x {Sweeps}: " +
                 $"inlined array lookup {inlined:F2} | unpatched call {unpatched:F2} | " +
                 $"postfix Off {off:F2} | Tick {tick:F2} | TickAndCycle {cycle:F2}");
        Log.Info($"so the mod costs {cycle - inlined:F2} ns per roll against an inlined vanilla " +
                 $"({cycle / inlined:F1}x): {unpatched - inlined:F2} of it lost inlining, " +
                 $"{off - unpatched:F2} the detour, {cycle - off:F2} the arithmetic");

        // Where the arithmetic goes, since it costing three times the detour is not what an add
        // and a hash should look like.
        var modulo = NanosPerRoll(() => SweepWrap(mask: false));
        var masked = NanosPerRoll(() => SweepWrap(mask: true));
        Log.Info($"the wrap alone: modulo {modulo:F2} ns, mask {masked:F2} ns, " +
                 $"so reducing mod int.MaxValue costs {modulo - masked:F2} ns of the " +
                 $"{cycle - off:F2} ns arithmetic");

        // Wide enough to ignore a loaded machine, tight enough that a real regression shows. The
        // detour is the dominant term and it does not vary much.
        if (cycle > inlined * 25 || cycle > 200)
            throw new AssertionException(
                $"a patched roll costs {cycle:F2} ns against {inlined:F2} inlined, which is far " +
                "more than a Harmony detour should. Something in the postfix is allocating, " +
                "boxing, or taking a lock.");
    }

    /// <summary>
    /// The postfix from <c>minimal/RNGTickMinimal.cs</c>, copied verbatim.
    ///
    /// <para>That file is outside the build on purpose -- a second <c>[HarmonyPatch]</c> class
    /// postfixing <c>RNG.Roll</c> would apply the offset twice -- so the only way to time it
    /// against the real one is to keep a copy here. The test below asserts the two produce
    /// identical rolls before it times them, which is what keeps the copy honest.</para>
    /// </summary>
    public static void MinimalAfterRoll(int tick, ref int __result)
    {
        if (!RNG.IsInitialized)
            return;

        unchecked
        {
            var cycle = tick >> 8;
            cycle = ((cycle >> 16) ^ cycle) * 73244475;
            cycle = ((cycle >> 16) ^ cycle) * 73244475;
            cycle = (cycle >> 16) ^ cycle;

            var sum = ((long)__result + tick + cycle) % int.MaxValue;
            __result = (int)(sum < 0 ? sum + int.MaxValue : sum);
        }
    }

    /// <summary>
    /// The minimal body with the shipped mod's mode check bolted on, and nothing else.
    ///
    /// <para>Sits between the two so the gap can be attributed: whatever this costs over
    /// <see cref="MinimalAfterRoll"/> is the static read and the switch, and whatever the shipped
    /// postfix costs over this is the call structure -- <c>AfterRoll</c> into
    /// <c>TickOffset.Apply</c> into <c>Mix</c> and <c>Wrap</c>.</para>
    /// </summary>
    public static void MinimalWithModeAfterRoll(int tick, ref int __result)
    {
        if (!RNG.IsInitialized)
            return;

        unchecked
        {
            long sum;
            switch (RNGTickConfig.Mode)
            {
                case OffsetMode.Off:
                    return;
                case OffsetMode.Tick:
                    sum = (long)__result + tick;
                    break;
                default:
                    var cycle = tick >> 8;
                    cycle = ((cycle >> 16) ^ cycle) * 73244475;
                    cycle = ((cycle >> 16) ^ cycle) * 73244475;
                    cycle = (cycle >> 16) ^ cycle;
                    sum = (long)__result + tick + cycle;
                    break;
            }

            sum %= int.MaxValue;
            __result = (int)(sum < 0 ? sum + int.MaxValue : sum);
        }
    }

    /// <summary>
    /// The flat body again, but guarded through <c>Volatile.Read</c> rather than a plain field
    /// read -- the workaround the woven build needs, isolated from everything else it changes.
    /// </summary>
    public static void MinimalWithVolatileGuardAfterRoll(int tick, ref int __result)
    {
        if (!Volatile.Read(ref RNG.IsInitialized))
            return;

        unchecked
        {
            long sum;
            switch (RNGTickConfig.Mode)
            {
                case OffsetMode.Off:
                    return;
                case OffsetMode.Tick:
                    sum = (long)__result + tick;
                    break;
                default:
                    var cycle = tick >> 8;
                    cycle = ((cycle >> 16) ^ cycle) * 73244475;
                    cycle = ((cycle >> 16) ^ cycle) * 73244475;
                    cycle = (cycle >> 16) ^ cycle;
                    sum = (long)__result + tick + cycle;
                    break;
            }

            sum %= int.MaxValue;
            __result = (int)(sum < 0 ? sum + int.MaxValue : sum);
        }
    }

    /// <summary>
    /// The shipped mod's structure, rebuilt inside this assembly.
    ///
    /// <para>The control the first measurement lacked. Every fast variant above lives in
    /// RNGTick.Test.dll and the shipped postfix lives in RNGTick.dll, so "the call into
    /// TickOffset costs 7.5 ns" had two candidate explanations riding together: the depth of the
    /// call chain, and the assembly boundary -- the mod is loaded from a stream out of a zip by
    /// the mod loader, which is not how the JIT usually meets an assembly.</para>
    ///
    /// <para>This mirrors the shipped shape exactly -- postfix, then a static read and a switch,
    /// then a separate wrap and a separate hash -- but compiled into the test assembly. Landing
    /// near the minimal figure means the boundary is what costs; landing near the shipped figure
    /// means the structure is.</para>
    /// </summary>
    private static class LocalOffset
    {
        public static OffsetMode Mode = OffsetMode.TickAndCycle;

        public const int Range = int.MaxValue;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Apply(int roll, int tick) => Mode switch
        {
            OffsetMode.Off => roll,
            OffsetMode.Tick => Wrap((long)roll + tick),
            _ => Wrap((long)roll + tick + Mix(tick >> 8)),
        };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Wrap(long sum)
        {
            sum %= Range;
            if (sum < 0)
                sum += Range;
            return (int)sum;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Mix(int input)
        {
            unchecked
            {
                input = ((input >> 16) ^ input) * 73244475;
                input = ((input >> 16) ^ input) * 73244475;
                return (input >> 16) ^ input;
            }
        }
    }

    /// <summary>
    /// Two levels instead of three: the postfix calls one method that holds the mode switch and
    /// the wrap, with only the hash left as a leaf call.
    ///
    /// <para>Which rung of the ladder the inliner gives up on decides what the fix has to be. If
    /// this lands near the flat version, folding <c>Wrap</c> into <c>TickOffset.Apply</c> is
    /// enough and the mod keeps one copy of its arithmetic. If it lands near the shipped one,
    /// nothing short of writing the whole body into the postfix will do.</para>
    /// </summary>
    private static class TwoLevel
    {
        public static OffsetMode Mode = OffsetMode.TickAndCycle;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Apply(int roll, int tick)
        {
            if (Mode == OffsetMode.Off)
                return roll;

            var sum = (long)roll + tick;
            if (Mode != OffsetMode.Tick)
                sum += Mix(tick >> 8);

            sum %= int.MaxValue;
            return (int)(sum < 0 ? sum + int.MaxValue : sum);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Mix(int input)
        {
            unchecked
            {
                input = ((input >> 16) ^ input) * 73244475;
                input = ((input >> 16) ^ input) * 73244475;
                return (input >> 16) ^ input;
            }
        }
    }

    public static void TwoLevelAfterRoll(int tick, ref int __result)
    {
        if (!RNG.IsInitialized)
            return;

        __result = TwoLevel.Apply(__result, tick);
    }

    public static void LayeredAfterRoll(int tick, ref int __result)
    {
        if (!RNG.IsInitialized)
            return;

        __result = LocalOffset.Apply(__result, tick);
    }

    /// <summary>
    /// <b>What the abstractions cost.</b> The shipped postfix reads a mode off a static, switches
    /// on it, and calls out to <c>TickOffset.Apply</c>, which calls <c>Mix</c> and <c>Wrap</c>.
    /// The minimal build does the same arithmetic inline in one method with no mode at all. Same
    /// answers, so the difference is the structure.
    /// </summary>
    [GameTest]
    public static void WhatTheAbstractionsCost()
    {
        var minimalPostfix = AccessTools.Method(typeof(RollBenchmarks), nameof(MinimalAfterRoll));

        // A reference set from the shipped mod, to hold the minimal one to.
        var expected = Rolls.With(OffsetMode.TickAndCycle, () =>
            Samples.Select(s => RNG.Roll(s.X, s.Y, s.Tick)).ToArray());

        var minimal = InsteadOfThePostfix(minimalPostfix, () =>
        {
            for (var i = 0; i < Samples.Length; i++)
            {
                var (x, y, tick) = Samples[i];
                var got = RNG.Roll(x, y, tick);
                if (got != expected[i])
                    throw new AssertionException(
                        $"minimal and shipped disagree at ({x},{y},{tick}): {got} vs {expected[i]}. " +
                        "The copy of the minimal postfix in this file has drifted from " +
                        "minimal/RNGTickMinimal.cs, and the timing below would be comparing two " +
                        "different mods.");
            }

            return NanosPerRoll(SweepViaRoll);
        });

        // The same body plus the mode check, to split the gap into its two halves.
        var withMode = InsteadOfThePostfix(
            AccessTools.Method(typeof(RollBenchmarks), nameof(MinimalWithModeAfterRoll)),
            () => Rolls.With(OffsetMode.TickAndCycle, () => NanosPerRoll(SweepViaRoll)));

        // The same flat body with only the guard changed, to price the workaround on its own.
        var volatileGuard = InsteadOfThePostfix(
            AccessTools.Method(typeof(RollBenchmarks), nameof(MinimalWithVolatileGuardAfterRoll)),
            () => Rolls.With(OffsetMode.TickAndCycle, () => NanosPerRoll(SweepViaRoll)));

        // And the shipped structure recompiled here, to tell call depth from assembly boundary.
        var layered = InsteadOfThePostfix(
            AccessTools.Method(typeof(RollBenchmarks), nameof(LayeredAfterRoll)),
            () => NanosPerRoll(SweepViaRoll));

        // One rung shallower: mode and wrap together, only the hash left as a call.
        var twoLevel = InsteadOfThePostfix(
            AccessTools.Method(typeof(RollBenchmarks), nameof(TwoLevelAfterRoll)),
            () => NanosPerRoll(SweepViaRoll));

        var full = Rolls.With(OffsetMode.TickAndCycle, () => NanosPerRoll(SweepViaRoll));
        var bare = NanosPerRoll(() => SweepViaArray(
            (int[])AccessTools.Field(typeof(RNG), "RNGVolume").GetValue(null)!));

        Log.Info($"ns per roll: inlined lookup {bare:F2} | minimal {minimal:F2} | " +
                 $"minimal+mode {withMode:F2} | minimal+mode+volatile {volatileGuard:F2} | " +
                 $"two levels {twoLevel:F2} | " +
                 $"three levels, this assembly {layered:F2} | shipped {full:F2}");
        Log.Info($"structure vs boundary: rebuilding the shipped shape locally costs " +
                 $"{layered - withMode:F2} ns over the flat version, leaving {full - layered:F2} ns " +
                 "that only the assembly boundary explains");
        Log.Info($"of the mod's {full - bare:F2} ns overhead: {minimal - bare:F2} is the detour and " +
                 $"the arithmetic, {withMode - minimal:F2} the config read and switch, " +
                 $"{full - withMode:F2} the call into TickOffset. Abstractions total " +
                 $"{full - minimal:F2} ns ({(full - minimal) / (full - bare) * 100:F0}%).");

        if (minimal > full * 1.5)
            throw new AssertionException(
                $"the minimal postfix measured {minimal:F2} ns against the shipped {full:F2}, which " +
                "is backwards by more than noise explains; the benchmark is measuring the wrong thing");
    }

    /// <summary>
    /// Removes this mod's postfix, runs <paramref name="body"/>, and puts it back.
    ///
    /// <para>Restoration is asserted, not hoped for. Every other test in this suite depends on the
    /// patch being installed, so leaving it off would turn one benchmark into a suite-wide
    /// failure with no obvious cause. The Harmony instance is built with the mod's own id so that
    /// ownership survives the round trip and <c>PatchTests</c> still recognises the postfix
    /// afterwards.</para>
    /// </summary>
    private static T WithoutThePostfix<T>(Func<T> body) => InsteadOfThePostfix(null, body);

    /// <summary>
    /// Takes this mod's postfix off, optionally puts <paramref name="replacement"/> on in its
    /// place, runs <paramref name="body"/>, and restores the original either way.
    /// </summary>
    private static T InsteadOfThePostfix<T>(MethodInfo? replacement, Func<T> body)
    {
        var harmony = new Harmony(RNGTick.ModEntry.ModId);
        var postfix = AccessTools.Method(typeof(RNGPatches), nameof(RNGPatches.AfterRoll));
        var targets = RNGPatches.TargetMethods().ToList();

        // The unoffset value, captured now. It cannot be obtained once the swap is in: a
        // replacement postfix knows nothing about OffsetMode, so asking for Off afterwards
        // returns an offset roll like any other and the check would read as "nothing applied".
        const int X = 11, Y = 13, Tick = 4242;
        var raw = Rolls.With(OffsetMode.Off, () => RNG.Roll(X, Y, Tick));

        foreach (var target in targets)
        {
            harmony.Unpatch(target, postfix);
            if (replacement != null)
                harmony.Patch(target, postfix: new HarmonyMethod(replacement));
        }

        try
        {
            // Confirm the swap took. With no replacement the roll must come back raw; with one
            // it must not, or the measurement is of whatever was there before.
            var offsetNow = RNG.Roll(X, Y, Tick) != raw;
            if (offsetNow != (replacement != null))
                throw new AssertionException(replacement == null
                    ? "the postfix is still applying an offset after Unpatch, so the unpatched " +
                      "measurement would be meaningless"
                    : "the replacement postfix is not being applied, so this would measure nothing");

            return body();
        }
        finally
        {
            foreach (var target in targets)
            {
                if (replacement != null)
                    harmony.Unpatch(target, replacement);
                harmony.Patch(target, postfix: new HarmonyMethod(postfix));
            }

            var restored = Rolls.With(OffsetMode.TickAndCycle, () => RNG.Roll(X, Y, Tick));
            if (restored == raw)
                throw new AssertionException(
                    "the postfix did not go back on after the benchmark. Every later test in this " +
                    "suite assumes it is installed and would fail for the wrong reason.");
        }
    }
}

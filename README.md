# RNGTick

An Atomcraft mod that folds the current tick into every deterministic roll, so a pixel's luck
stops being a property of where it is standing.

## The problem

`RNG.Roll(x, y, tick)` is not a generator. It is a lookup into a fixed 512 x 512 x 256 block of
integers, filled once at startup from `new Random(12345)`:

```csharp
int num3 = tick & 0xFF;                      // <- eight bits of tick, and no more
return RNGVolume[(num * 512 + num2) * 256 + num3];
```

So a pixel that does not move has **256 rolls, forever**, cycling every four seconds of game
time. Callers reduce that to what they need -- `% 240` for a reaction, `& 0x7F` for a percentage
check, `% 2` for a coin flip -- and the residues among 256 fixed numbers are lumpy. The lumpiness
never averages out, because it is not sampling noise; it is the same 256 numbers again.

Measured in the running game, 144 positions over 32768 ticks:

| consumer | spread across positions | worst position |
| --- | --- | --- |
| reaction at `Probability 240` | sd 0.0047 against a mean of 0.0042 | **34% of positions never fire at all** |
| `RollPct` at 5 in 128 | sd 0.0113 against a mean of 0.0377 | 1.2% where 3.9% was intended |
| coin flip, `% 2` | sd 0.0296 | a persistent 57:43 lean |

The first row is the one that matters. At a third of all positions that reaction is not unlikely,
it is **impossible**, and stays impossible for the life of the world. Move the same setup one cell
over and it works.

## What this mod does

Postfixes both `RNG.Roll` overloads, offsets the result, and wraps it back into
`[0, int.MaxValue)` -- the range `Random.Next()` promises and every caller was written against.
That last part is not cosmetic: `RandomDeterministic` uses a roll as an array index.

| mode | offset added | |
| --- | --- | --- |
| `Off` | none | vanilla, byte for byte. Installed but inert. |
| `Tick` | `tick` | |
| `TickAndCycle` | `tick + Mix(tick >> 8)` | **the default** |

Set through `RNGTick.RNGTickConfig.Mode`.

**Why the tick alone is not enough.** If the modulus divides 256 then `tick % m` is already
decided by the `tick & 255` that chose the value, so the outcome stays a function of
`tick & 255`: still 256 outcomes on a 256-tick cycle, and elapsed time adds no samples. That is
not a corner case -- `RNG.RollPct` is `Roll(..) & 0x7F`, 58 call sites against 30 that call `Roll`
directly. `TickAndCycle` adds a hash of the cycle number, which is exactly the information the
lookup index throws away. The hash is the game's own, from `Simulation.PRNG`.

## Performance

Measured by `test/RollBenchmarks.cs` on an idle machine, over a 64x64 sweep per tick:

| | ns per roll |
| --- | ---: |
| inlined array lookup, what vanilla costs | 3.2 |
| **shipped postfix** | **5.4** |

About **2.2 ns**, or 1.7x an inlined vanilla roll. In the condensation fixture -- about as
roll-heavy as a region gets, 1922 rolls per tick -- that is roughly 1% of the tick.

**Build Release, and nothing else matters.** A Debug assembly carries `DebuggableAttribute` with
`DisableOptimizations`, which switches the JIT off for that assembly entirely: nothing is inlined
and the same postfix measures **22 ns**. `build.sh` defaults to Release for this reason.

With optimizations on, everything that looks like it should cost something does not. One flat
method measures 5.40, the shipped three-method chain 5.43; the JIT folds
`AfterRoll` -> `Apply` -> `Wrap`/`Mix` and **the split into `TickOffset` costs 0.03 ns**.
`minimal/RNGTickMinimal.cs` is the control for that comparison, and the benchmark asserts it
produces identical rolls before timing it. There is no abstraction penalty to recover here, which
is worth stating because the opposite was believed for a while on the strength of careful
measurements of a Debug build.

## Install

Needs [GodotMonoModLoader](https://github.com/sacroimper/GodotMonoModLoader). Drop
`build/RNGTick.zip` into `<user data>/Mods/`, still zipped. The mod ships no materials, reactions
or translations -- one class and one postfix -- so there is nothing to configure and nothing to
uninstall beyond deleting the zip.

## Building and testing

```sh
./build.sh              # mod and tests
./run-tests.sh          # run the suite
```

Tests use the [TestHarness](https://github.com/sparr/atomcraft-mod-TestHarness) and run inside the real game, headless. They ask
`Atomcraft.RNG` for rolls rather than reimplementing the lookup: a test that computed its own
expected values would pass against a mod that patched nothing.

That is the failure worth guarding. Both `Roll` overloads carry `AggressiveInlining`, and a
Harmony detour on such a method is defeated by any caller compiled with the original body pasted
in -- silently, with the patch still reporting itself installed. So every check that can go
through one of the game's own wrappers (`RollPct`, `RollFloat`, `RandomDeterministic`) does, and
compares the wrapper's answer against the patched `Roll` rather than against a number the test
computed.
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

## What it fixes, and what it cannot

A position's rate is wrong for two separable reasons.

**Systematic bias** -- its 256 values are drawn from a distribution that is not uniform mod `m`.
Adding the tick removes this entirely, whatever shape it takes: over a 256-tick cycle the offset
visits every residue exactly twice, so the outcome is the position's own distribution convolved
with a uniform one. A position holding one value 256 times has a single outcome in vanilla and
sweeps every residue once offset; one confined to 0-63 fires low checks at exactly twice their
rate and comes back to nominal.

**Sampling noise** -- 256 samples is not many. Nothing about adding the tick touches this, and for
a modulus dividing 256 the outcome period stays 256 however long the world runs.

The shipped volume has only the second. Its spread matches `sqrt(p(1-p)/256)` to within a couple
of percent, which is the signature of pure 256-sample noise and leaves no room for a systematic
component -- unsurprising, since the volume is `new Random(12345)` output and already independent
of the index. So on a power-of-two modulus the tick alone relocates the noise rather than reducing
it: across 4096 positions, vanilla and `Tick` rates correlate at -0.005 and the worst position is
identical in both.

**The one precondition** is that a position's values not depend on the index selecting them.
`V[i] = i % 64` leaves 64 residues unreachable in vanilla and *still* leaves exactly 64 after the
offset, while collapsing a fair coin flip to always-heads. Nothing in the shipped volume looks
like that, but the assumption is load-bearing, so it is exercised rather than asserted in prose.

## How long it takes to work

`tick >> 8` is constant inside a 256-tick cycle, so the cycle term contributes one fixed rotation
there: the same histogram, relabelled. Measured over single cycles the spread is 0.98x to 1.10x
vanilla's. **Inside one cycle this mod does nothing.**

That is a floor, not a shortfall -- a position has 256 values available to it in 256 ticks, so
nothing can make it behave like more. The cycle term's only job is to make successive cycles
differ so counts accumulate:

| cycles | game time at 60 tps | vanilla | `TickAndCycle` | ideal for that many samples |
| ---: | ---: | ---: | ---: | ---: |
| 1 | 4.3 s | 0.01219 | 0.01213 | 0.01211 |
| 2 | 8.5 s | 0.01219 | 0.00839 | 0.00856 |
| 32 | 2.3 min | 0.01219 | 0.00198 | 0.00214 |
| 2048 | 2.4 h | 0.01219 | 0.00027 | 0.00027 |

From two cycles it tracks the ideal curve, and between two and thirty-two it slightly beats it:
summing distinct rotations of one histogram cancels more thoroughly than independent draws.

So: **nothing for anything resolving in under about four seconds of game time, a great deal for
anything a player waits on.** Which is the right way round.

## The one you can see in the game

Vanilla condenses noble gases out of cold empty air. One line in `Simulation.SimulateCoords`,
reached only for a cell holding nothing:

```csharp
if (y.IsBelowSpace() && y.IsAboveWorkshop() && RNG.Roll(x, y, tick) % 100000 == 1)
    TryCondenseNobleGasOutOfAir(field, x, y, tick);
```

Build a sealed box, chill it below 165 K, wait. That is a thing a player does on purpose. Except
in vanilla it is not a one-in-a-hundred-thousand chance, it is a property of the address: either
one of the position's 256 rolls is congruent to 1 mod 100000 or the box never produces a single
atom. Measured: **one position in 443**.

`test/CondensationTests.cs` builds 1922 cold traps in a single chunk. Vanilla: **10 can ever
condense**, exact rather than sampled, because 256 rolls is a set you can read to the end. With
the mod: **584 in 35668 ticks**, against 30.0% predicted.

## What it does not change

- **Determinism.** Every mode is a pure function of position and tick. Clients at the same tick
  agree, a roll asked for twice in a tick answers the same, and `--determinism` passes.
- **The average.** Mean rates stay within a quarter of nominal in every mode, and in practice move
  by a percent or two.
- **Anything without a position in it.** `RollLowerThanChanceOutOf1024_TimeOnly` and
  `RollIntWithinRange` read a tick-only table and cannot have per-position bias.

## What it does change

**Outcomes**, which is the point, with consequences worth stating:

- A world played with this mod evolves differently from the same world without it, from the first
  tick. Nothing is corrupted and no save is invalidated -- the mod stores nothing -- but a scene
  will not replay identically across installing or removing it.
- **Every player in a multiplayer session must run the mod, in the same mode.** A mismatch is a
  desync.
- Worldgen consuming deterministic rolls produces different terrain.

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
./build.sh              # mod, tests, conformance suite
./run-tests.sh          # this project's tests. About 40 seconds. The everyday loop.
./run-tests.sh --all    # plus the harness's own suite. About 2m45. Before calling anything done.
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

**Run `--all` before calling anything done.** Every test here is a region test that never starts a
session, so the harness's session tests exercise nothing in this mod and cost about 120 of the 137
seconds -- but this project patches `RNG.Roll` underneath them, which is how it broke one of them
once.

## The conformance suite

`conformance/` is a separate mod that names none of this one: no project reference, no mention in
its `mod.json`, which depends only on the harness. Install it beside any candidate fix and it
reaches a verdict; install it alone and it fails, because stock Atomcraft does not pass.

Its assertions are **absolute** rather than diffs against stock, so it stays valid against a
version of the game that fixes this itself -- there is no stock to compare against once the stock
is the thing being judged. A fair generator still leaves a spread of `sqrt(p(1-p)/n)` between
positions and no fix can beat that, so each test asks for a spread within three times that floor.
The two cases are nowhere near each other:

| | stock | with this mod |
| --- | --- | --- |
| reaction at `Probability 240` | 13.3x the floor, 49 of 144 positions dead | 1.0x, none dead |
| `RollPct` at 5 in 128 | 10.5x | 0.9x |
| coin flip | 10.7x | 0.8x |

`RollPct` is tested separately on purpose: 128 divides 256, so a fix that only adds the tick
passes the reaction test and fails that one.

One test does compare against stock, and is the only one that can be inapplicable -- it tells a
mod that is not reaching `RNG.Roll` from one that is. If `RNGVolume` is gone the game has changed
its RNG rather than had it patched, and it says so and passes.

### Running it against something else

`RNGConformance.zip` from the [releases](https://github.com/sparr/atomcraft-mod-RNGTick/releases)
is self-contained and needs none of this repository. Alongside it you need a game patched with
[GodotMonoModLoader](https://github.com/sacroimper/GodotMonoModLoader) and
[TestHarness.zip](https://github.com/sparr/atomcraft-mod-TestHarness/releases), which is what
discovers and runs the tests.

Put all three in `<user data>/Mods/`, still zipped, plus whatever you are judging:

```
Mods/
  TestHarness.zip
  RNGConformance.zip
  SomeCandidateFix.zip      # or nothing, to see the stock game fail
```

Then launch the game with the loader and the test runner:

```sh
AtomCraft.exe -s GodotMonoModLoader.gd --headless -- --atomtest-run --atomtest-filter='^RNGConformance\.'
```

Results land in `user://logs/godot.log`: one `##ATOMTEST##` JSON line per test, and a
human-readable `[conformance]` line per measurement giving the mean, the spread, how many times
the sampling floor that is, and how many positions never produced the outcome at all. The exit
code is the suite's, so it works in CI.

Six tests. Against the stock game three fail on spread and one on nothing reaching `RNG.Roll`;
the two that pass are determinism and range, which stock does not get wrong.

## A note on the game, found along the way

`RollLowerThanChanceOutOf1024_TimeOnly` is
`return (RNGTimeOnly[tick & 0xFFF] &= 1023) < chance;` -- a compound assignment, confirmed in IL.
Every call writes back into the table. Masking is idempotent so the answers never change, but a
method the simulation calls per pixel, from inside `Parallel.ForEach` over chunks, writes to a
shared array on every call.
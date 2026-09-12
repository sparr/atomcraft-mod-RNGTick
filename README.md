# RNGTick

An Atomcraft mod that folds the current tick into every deterministic roll, so that a pixel's
luck stops being a property of where it is standing.

## The problem

`RNG.Roll(x, y, tick)` is not a generator. It is a lookup into a fixed 512 x 512 x 256 block of
integers, filled once at startup from `new Random(12345)`:

```csharp
public static int Roll(int posX, int posY, int tick)
{
    int num  = posX & 0x1FF;
    int num2 = posY & 0x1FF;
    int num3 = tick & 0xFF;                      // <- eight bits of tick, and no more
    return RNGVolume[(num * 512 + num2) * 256 + num3];
}
```

The tick index is masked to eight bits. So a pixel that does not move has **256 rolls, forever**,
cycling every 256 ticks, about four seconds of game time. Callers then reduce that value:
`% 240` for a reaction with `Probability 240`, `& 0x7F` for a percentage check, `% 2` for a
coin flip. Whatever they do, they are reducing the same 256 numbers every time, and the residues
among 256 samples are lumpy. That lumpiness never averages out, because it is not sampling
noise; it is the same 256 numbers again.

Measured in the running game, over 144 positions and 32768 ticks (see `test/BiasTests.cs`):

| Consumer | spread across positions | worst position |
| --- | --- | --- |
| reaction with `Probability 240` | sd 0.0047 against a mean of 0.0042 | **34% of positions never fire at all** |
| `RollPct` check at 5 in 128 | sd 0.0113 against a mean of 0.0377 | 1.2% where 3.9% was intended |
| coin flip, `% 2` | sd 0.0296 | a persistent 57:43 lean |

The first row is the one to look at. At a third of all positions a reaction with
`Probability 240` is not unlikely, it is **impossible**, and it stays impossible for the life of
the world however long the player waits. Move the same setup one cell over and it works.

## What this mod does

It postfixes both `RNG.Roll` overloads and offsets the result, then wraps it back into
`[0, int.MaxValue)`, the range `Random.Next()` promises and every caller was written against.

Three modes, set through `RNGTick.RNGTickConfig.Mode`:

| Mode | Offset added | |
| --- | --- | --- |
| `Off` | none | vanilla, byte for byte. The mod installed but inert. |
| `Tick` | `tick` | the narrow fix. |
| `TickAndCycle` | `tick + Mix(tick >> 8)` | **the default.** |

### What the tick offset actually fixes, and what it cannot

A position's rate can be wrong for two separate reasons, and they behave completely differently
under the offset.

**Systematic bias -- the position's 256 values are drawn from a distribution that is not uniform
mod `m`.** Adding the tick removes this entirely, whatever shape it takes. Over a 256-tick cycle
the offset `i` visits every residue mod 128 exactly twice, so the outcome distribution is the
position's own value distribution *convolved with the uniform distribution*, and convolving
anything with uniform gives uniform. All three rows below are asserted in `test/BiasTests.cs`:

| Position holding | vanilla | `+tick` |
| --- | --- | --- |
| all 256 entries equal | one outcome, forever. Not biased, decided | every residue swept exactly evenly |
| 0-63, four times each, shuffled | residues 64-127 unreachable; every low check fires at **exactly twice** its intended rate | residue 64+ reached about half the time, rate back on nominal, flat to ~2% across an ensemble |
| 0-63, four times each, **in index order** | 64 residues unreachable, coin flip exactly fair | **still exactly 64 unreachable**, and the coin flip now lands the same way *every time* |

That third row is the precondition, and it is the only place this argument can break: convolution
needs the value distribution to be *independent* of the index. `V[i] = i % 64` is a function of
the index, so adding the index reinforces the structure rather than cancelling it -- strictly
worse on parity, not merely unimproved. Nothing in the shipped volume looks like that, since it is
`new Random(12345)` output and independent of the index by construction, but the assumption is
load-bearing and so it is exercised rather than asserted in prose. The cycle term needs no such
assumption: it clears the index-correlated cell too.

**Sampling noise -- 256 samples is not many.** Adding the tick cannot touch this. If `m` divides
256, `tick % m` is already determined by the `tick & 255` that chose the value, so the outcome
stays a function of `tick & 255`: still 256 outcomes on a 256-tick cycle, however long the world
runs. There is nothing for elapsed time to amortize, because time adds no samples.

### Which of the two the shipped game has

Only the second. If a position's spread were nothing but the noise of `N` samples it would equal
`sqrt(p(1-p)/N)` exactly, and for the `RollPct` check at 5 in 128 it does, with N = 256:

| | measured spread | noise of 256 samples | noise of 32768 samples |
| --- | --- | --- | --- |
| vanilla | 0.01219 | **0.01211** | 0.00107 |
| `Tick` | 0.01213 | **0.01211** | 0.00107 |
| `TickAndCycle` | 0.00107 | 0.01211 | **0.00107** |

(4096 positions, 32768 ticks. `test/BiasTests.cs` asserts the same relation in-game over 144.)

So the volume the game ships carries **no systematic bias at all** -- it is `new Random(12345)`
output, already uniform and already independent of the index -- and the tick offset arrives to
remove a component that is zero. Its effect on a power-of-two modulus is to relocate the
remaining noise: vanilla and `Tick` per-position rates correlate at -0.005 across 4096 positions,
and the worst position is 0.00391 in both. A different set of positions is unlucky, by the same
amount.

That still leaves `Tick` worth having on its own terms. It is a guarantee rather than a variance
reduction: no position can ever be systematically biased, whatever ends up in the volume. The
game can load a volume from disk (`RNG.LoadFromFile`), and a reseed or a hand-built table is
exactly the case where the guarantee would start to matter. It just does not pay off on the
table shipped today.

### What the cycle term does instead

It attacks the other term. `RNG.RollPct` is `Roll(..) & 0x7F`, m = 128, and it is the most-used
roll in the game -- 58 call sites against 30 that call `Roll` directly -- so on the shipped
volume `Tick` leaves nearly half the roll sites no fairer than it found them.

`TickAndCycle` adds a hash of `tick >> 8` as well: the number of the 256-tick cycle, which is
precisely the information the lookup index throws away. The offset is then not a function of
`tick & 255` for any modulus. The hash is the game's own, lifted from `Simulation.PRNG`.

Measured, same 144 positions and 32768 ticks, spread across positions (lower is fairer):

| | vanilla | `Tick` | `TickAndCycle` |
| --- | --- | --- | --- |
| `Probability 240` | 0.00472, 49 of 144 positions dead | 0.00112, none dead | 0.00035, none dead |
| `RollPct` at 5 in 128 | 0.01125 | 0.01107 (**relocated, not reduced**) | 0.00094 |
| coin flip | 0.02958 | 0.03116 (**relocated, not reduced**) | 0.00214 |

The two `Tick` figures are the noise of 256 samples in both columns; the `TickAndCycle` ones are
the noise of 32768. All of it is asserted in the suite -- the spread, the correlation, and the
`sqrt(p(1-p)/N)` relation that ties the account together -- so if a future change ever did make
`Tick` fix a power-of-two modulus, or the volume ever did acquire a systematic bias, the tests
fail and ask to be rewritten rather than going on quietly asserting an account that no longer
holds.

If you want only what the tick alone does, set `RNGTickConfig.Mode = OffsetMode.Tick` and the
mod does exactly that and nothing more.

### How long it takes to work

The offset is `tick + Mix(tick >> 8)`, and `tick >> 8` is constant inside a 256-tick cycle. So
within one cycle the cycle term contributes a **fixed rotation**: a position's 256 outcomes are
`Tick` mode's outcomes with the residue labels shuffled round, the same histogram wearing
different numbers. Measured over single cycles, `TickAndCycle`'s spread is 0.98x to 1.10x
vanilla's. Inside one cycle this mod does nothing at all.

That is a floor rather than a shortfall. A position has exactly 256 values available to it in
256 ticks, so no offset scheme of any kind can make it behave like more than 256 samples. The
only thing the cycle term can do is make *successive* cycles differ so the counts accumulate
instead of repeating, and that is what it does:

| cycles | ticks | game time at 60 tps | vanilla | `TickAndCycle` | ideal for that many samples |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 256 | 4.3 s | 0.01219 | 0.01213 | 0.01211 |
| 2 | 512 | 8.5 s | 0.01219 | 0.00839 | 0.00856 |
| 8 | 2048 | 34 s | 0.01219 | 0.00360 | 0.00428 |
| 32 | 8192 | 2.3 min | 0.01219 | 0.00198 | 0.00214 |
| 128 | 32768 | 9.1 min | 0.01219 | 0.00107 | 0.00107 |
| 2048 | 524288 | 2.4 h | 0.01219 | 0.00027 | 0.00027 |

From two cycles on it tracks the ideal curve, and between two and thirty-two cycles it beats it
slightly -- summing distinct rotations of one histogram cancels more thoroughly than independent
draws do, the same reason stratified sampling beats random sampling.

The practical reading: **this mod does nothing for anything that resolves in under about four
seconds of game time, and a great deal for anything a player waits on.** Which is the right way
round. A grain of sand choosing a direction once does not care that its odds were biased; a
reaction the player is standing in front of for ten minutes does.

### The one you can see in the game

There is a mechanic in vanilla that condenses noble gases out of cold empty air. One line in
`Simulation.SimulateCoords`, reached only for a cell holding nothing:

```csharp
if (y.IsBelowSpace() && y.IsAboveWorkshop() && RNG.Roll(x, y, tick) % 100000 == 1)
    TryCondenseNobleGasOutOfAir(field, x, y, tick);
```

Build a sealed box, chill the inside below 165 K, wait. That is a thing a player does on purpose.

Except in vanilla it is not a one-in-a-hundred-thousand chance, it is a property of the address.
A position only ever produces the 256 rolls its slice of the volume holds, so either one of those
256 is congruent to 1 mod 100000 or the box will never produce a single atom, however long anyone
leaves it. Measured: **one position in 443**. Move the same box one cell over and it works.

`test/CondensationTests.cs` builds 1922 cold traps in a single chunk and counts. Vanilla: **10 of
1922 can ever condense** -- and that is exact rather than sampled, because 256 rolls is a set you
can read to the end. With the mod: **584 of them in 35668 ticks**, against 30.0% predicted.

It also turns out something always condenses once the gate opens.
`TryCondenseNobleGasOutOfAir` gates again on `RNG.RollPct` and compares it against 80, 160, 320
and upward -- but `RollPct` is `Roll & 0x7F`, at most 127, so every threshold past the first is
met unconditionally. Below 4 K you get helium on a 80-in-128 roll and neon otherwise. Ours came
out 426 helium to 158 neon rather than the 62.5/37.5 that implies, because the roll that opens
the gate is the same roll that picks the gas.

## What it does not change

- **Determinism.** Every mode is a pure function of position and tick. The same roll asked for
  twice in a tick answers the same, and two machines at the same tick agree. `--determinism`
  passes.
- **The average.** The point is to move luck around, not to create it. Mean rates are asserted
  to stay within a quarter of nominal in every mode, and in practice they move by a percent or
  two.
- **Anything without a position in it.** `RollLowerThanChanceOutOf1024_TimeOnly` and
  `RollIntWithinRange` read a different table indexed by the tick alone. They cannot have
  per-position bias, and every cell asking on a given tick is meant to get the same answer, so
  they are left alone.
- **The non-deterministic RNG.** `RNG.RollNonDeterministic` and `NonDeterministicRNG` are a
  plain `System.Random` used for cosmetics and name generation. Untouched.

## What it does change

**Outcomes.** That is the entire point, and it is worth being explicit about the consequences:

- A world played with this mod evolves differently from the same world played without it, from
  the first tick. Nothing is corrupted and no save is invalidated -- the mod stores nothing and
  the save format is untouched -- but a scene will not replay identically across installing or
  removing it.
- **Every player in a multiplayer session must run the mod, in the same mode.** A mismatch is a
  desync, for the same reason any simulation-changing mod is.
- Worldgen that consumes deterministic rolls will produce different terrain.

## Performance

The patch sits under the hottest method in the simulation. Measured by
`test/RollBenchmarks.cs` on an idle machine, over a 64x64 sweep per tick -- the access pattern a
region tick actually makes -- in nanoseconds per roll:

| | ns | |
| --- | ---: | --- |
| inlined array lookup | 3.7 | what vanilla costs when the JIT pastes `Roll` into its caller |
| unpatched call | 4.1 | a real call, no detour |
| postfix, `Off` | 8.4 | the detour and nothing else |
| postfix, `Tick` | 15.9 | |
| postfix, `TickAndCycle` | 22.2 | as shipped |

So about **19 ns per roll, or 6x an inlined vanilla one**. Two things about that contradict what
the shape of the code suggests:

- **Losing the inlining costs nothing.** `RNG.Roll` is marked `AggressiveInlining` and MonoMod
  clears that flag to detour it, which looks like it should be the expensive part. It is 0.3 ns.
  The body is an array index; a call to it is barely worse than having it pasted in.
- **The modulo is not the problem either.** `Wrap` reduces mod `int.MaxValue`, a 64-bit division
  the JIT cannot turn into a multiply, and that is 2 ns of the 14. Masking to 31 bits instead
  would save almost nothing.

### Half of it is the abstractions, and the compiler cannot help

`minimal/RNGTickMinimal.cs` is this mod with the modes, the config, and the split into
`TickOffset` taken out -- one postfix doing the arithmetic inline. It produces identical rolls,
asserted over 512 samples before it is timed, and `RollBenchmarks` swaps it in for the real one.
Adding rungs back one at a time:

| | ns |
| --- | ---: |
| inlined array lookup | 3.6 |
| minimal postfix, one method, no modes | 13.3 |
| plus the mode switch, still one method | **14.6** |
| two methods: postfix calls one that holds the switch and the wrap | 23.4 |
| three methods: the shipped shape, recompiled into the test assembly | 22.3 |
| shipped | 22.5 |

Three things fall out of that ladder.

**It is not the assembly boundary.** The shipped shape rebuilt inside the test assembly costs
22.3 against the real 22.5 -- 0.2 ns. Worth checking, since the mod is loaded from a stream out
of a zip rather than off disk, but it explains nothing.

**It is not the modes.** The static read and the three-way switch are 1.3 ns.

**It is the first call, and only the first.** Two levels costs the same as three. Whatever
`AfterRoll` calls, it pays about 8 ns for the call and nothing further for what happens below it,
`AggressiveInlining` on every method in the chain notwithstanding.

So there is no arrangement of methods that gets the speed back -- and no compiler switch either.
Roslyn does not inline; it emits `call` and leaves every such decision to the JIT, and
`MethodImplOptions.AggressiveInlining` is a request the JIT has declined here. Build-time options
exist -- a source generator emitting the flattened body, or IL weaving with Fody -- but both are
heavy machinery to avoid retyping fifteen lines.

The fix, if it is wanted, is the row in bold: put the arithmetic in `AfterRoll` and keep all three
modes. **22.5 to 14.6 ns, cutting the mod's overhead from 18.9 to 11.0.** The price is that the
arithmetic then exists twice, since `TickOffset` is what the tests and this document describe --
and the equivalence check in `RollBenchmarks` is what would catch the two drifting apart. It has
not been done, because both numbers are small against how rarely a normal world rolls.

Whether 19 ns matters depends entirely on roll density. In the condensation fixture, which is
about as roll-heavy as a region gets at 1922 rolls per tick, it is roughly 12% of the tick. A
normal world rolls far less than that. If a heavily-loaded world feels slower, `Off` is the A/B
test, and it recovers all but the 4 ns detour.

## Install

Needs [GodotMonoModLoader](https://github.com/sacroimper/GodotMonoModLoader). Drop
`build/RNGTick.zip` into `<user data>/Mods/`, still zipped.

The mod ships no materials, reactions, or translations -- it is one class and two postfixes --
so there is nothing to configure and nothing to uninstall beyond deleting the zip.

## Building and testing

```sh
./build.sh              # both mods
./run-tests.sh          # this mod's tests. 45 seconds. The everyday loop.
./run-tests.sh --all    # plus the harness's own suite. 2m15. Before a commit or a release.
```

**Run `--all` before calling anything done**, not just before a commit. Every test here is a
region test that never starts a session, so the harness's 15 session tests exercise nothing in
this mod and cost about 100 of the 135 seconds a full run takes -- but this project patches
`RNG.Roll` underneath them, and it has changed the harness itself (`Session.cs`,
`WorldFixtures.cs`), so both directions of breakage are real. An explicit
`-- --atomtest-filter=...` overrides the default narrowing.

Tests use the [TestHarness](../Testing) and run inside the real game, headless. They measure the
live `RNG.Roll` rather than reimplementing it, which is what makes them able to fail when the
patch is not actually taking effect.

Two things in the suite are worth knowing about:

- **`PatchTests` is mostly about inlining.** Both `RNG.Roll` overloads carry
  `MethodImplOptions.AggressiveInlining`, and a Harmony detour on such a method is defeated by
  any caller compiled with the original body pasted into it. The failure is silent: the patch
  reports itself installed, a direct call through it works, and the simulation quietly keeps
  rolling vanilla numbers. So every test that can go through one of the game's own wrappers
  (`RollPct`, `RollFloat`, `RandomDeterministic`) does, and checks the wrapper's answer against
  the patched `Roll` rather than against a number the test computed itself. It works because
  Harmony 2.4.2 sits on MonoMod, which clears the inlining flag as part of detouring, and
  because mods load a frame before `Game._Ready`, before any simulation code has been compiled.
- **The full suite passes with this mod installed**, harness tests included, which is worth
  re-checking rather than assuming after any change here. It did not always: an earlier
  `TestHarness.Test.VanillaBehavior.SolidsSlideDownASlope` dropped a grain on the peak of a
  slope with floor below it on both sides and expected it to travel right, which vanilla's rolls
  happened to do and this mod's did not. The harness has since moved the slope against the wall
  so the grain has no leftward option. That is the shape of interaction to expect from a mod
  that changes RNG outcomes: not a regression, a scene whose result was pinned to specific roll
  values. Run `./run-tests.sh -- --atomtest-filter=RNGTick` for this mod's tests alone.

## A note on the game, found along the way

Not acted on by this mod, and harmless, but surprising enough to write down.

**`RollLowerThanChanceOutOf1024_TimeOnly` writes back into its own table.** The body is
`return (RNGTimeOnly[tick & 0xFFF] &= 1023) < chance;` -- a compound assignment, not a
comparison against a masked copy, and the IL confirms it (`ldelema`, `dup`, `ldind.i4`,
`and`, `dup`, `stind.i4`). Every call overwrites that slot with its low ten bits.

Masking is idempotent, so the answers never change and nothing misbehaves. What it does mean is
that a method the simulation calls per pixel, from inside `Parallel.ForEach` over chunks, writes
to a shared array on every call. Threads racing here store the same value, so it is benign
today, but it is not a shape you would want to build on. It is used for growth rates.

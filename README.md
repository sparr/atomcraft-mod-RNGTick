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

## How long it takes to work, and what it costs

`tick >> 8` is constant inside a 256-tick cycle, so the cycle term contributes one fixed rotation
there: the same histogram, relabelled. A position has 256 values available to it in 256 ticks and
no offset scheme can make it behave like more, so **inside one cycle this mod cannot help.** The
cycle term's only job is to make successive cycles differ, so that counts accumulate instead of
repeating.

Since the 2026-09-12 build that is not quite the whole story, because inside one cycle the mod is
now measurably *worse* than vanilla for some consumers. The game fills its RNG volume with a
shuffled permutation of 0-255 per position, so where the modulus divides 256 vanilla is not
sampling at all -- `Roll & 0x7F` visits every value exactly twice at every position, and the
spread between positions is **exactly zero**, which is better than any generator can be. Rotating
a permutation by a per-position constant gives back an ordinary 256-sample draw, so the mod lands
on the sampling floor instead. Measured over 144 positions:

| consumer | vanilla | `TickAndCycle`, one cycle | `TickAndCycle`, 128 cycles |
| --- | ---: | ---: | ---: |
| `RollPct` at 5 in 128, `m` divides 256 | **0.00000**, at any length | 0.01075 to 0.01210 | 0.00105 |
| reaction at `Probability 240`, `m` does not | 0.00385, **44 of 144 positions dead** | | 0.00034, none dead |

The sampling floor is 0.01211 for 256 samples and 0.00107 for 32768, so both `TickAndCycle` figures
in the top row are exactly what 256 and 32768 draws leave behind. It never gets below vanilla's
zero, and waiting does not help, because vanilla's zero does not grow either.

**That cost is accepted.** It is real and it is permanent for power-of-two consumers, and it buys
the second row, where the balanced volume does nothing at all: a modulus that does not divide 256
still leaves a position locked out of outcomes entirely, and no amount of waiting reaches them.
That is where the shipped rare reactions live, and it is what this mod is for. Pick `Off` if a
world cares more about the first row than the second.

So: **nothing for anything resolving in under about four seconds of game time, a great deal for a
rare event a player waits on, and a little worse than stock for frequent power-of-two checks.**

## The one you can see in the game

`Compacted Dirt Decomposition` ships with the game: one Compacted Dirt above 300 K becomes one
Dirt, at `Probability 1000`. `BaseMaterial.IsReactionValid` rejects it whenever
`RNG.Roll(posX, posY, tick) % 1000 != 0`, on the raw tick.

One input cell and one output cell makes it the cleanest case there is -- a site is a **single
pixel**, so exactly one position's rolls decide it. `test/ReactionTests.cs` builds 1922 of them as
a checkerboard in one chunk, each walled off from its neighbours so no site can borrow another's
luck, and runs the same positions under each mode for ten 256-tick cycles:

| | sites that ever reacted | |
| --- | ---: | --- |
| vanilla | **433 of 1922 (22.5%)** | all of them inside the first 256 ticks, and **not one more in the 2304 that followed** |
| `Tick` | 1760 (91.6%) | |
| `TickAndCycle` | 1782 (92.7%) | against 92.3% for a generator that behaves like a probability |

One run. The counts move a point or two with whichever slice of the world the harness hands the
test, but the shape does not: vanilla has never exceeded its 22.6% ceiling and has never gained a
site after the first cycle.

The vanilla row is the whole argument, and it is stronger than a low rate. The set stopped growing
after one cycle and never moved again, because a position has 256 rolls and nothing else: either
one of them is congruent to 0 mod 1000 or the reaction cannot happen there, ever. Over 77% of the
world is locked out of a recipe the game describes as one in a thousand.

Of the 84 shipped recipes carrying a probability, 14 are rarer than 1 in 100. `Compost from Fallen
Leaves` at 1 in 10000 -- nine leaves, no heat rig, the cheapest thing in the game to leave running
-- locks out about 97% of positions.

Everything above is measured in the game's own simulation. The test places the game's own
material, lets the game's own `Step` run, and counts pixels; it reproduces none of the game's
arithmetic, which is the one way a test like this can report a fix that is not there. The noble
gas condensation gate used to be the showcase here, and the 2026-09-12 build fixed it upstream by
folding the cycle number into that one call site -- this mod's idea, applied by hand to a single
consumer. `test/CondensationTests.cs` now runs both arms through the simulation and asserts only
that the mod keeps up with the game. Over 22315 ticks the mod's arm lands on the arithmetic every
time -- 19.0%, 19.4%, 19.5% and 20.4% across four regions, against 20.0% predicted -- while
vanilla's ranges from 17.2% to 24.4%, because the game's own fix reuses one row of the table per
cycle and the traps in a region rise and fall together.

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

Needs [GodotMonoModLoader](https://github.com/sacroimper/GodotMonoModLoader).

Download `RNGTick.zip` from the
[releases](https://github.com/sparr/atomcraft-mod-RNGTick/releases) and drop it into
`<user data>/Mods/`. **Do not extract it** -- the loader reads mods straight out of the zip, and
an extracted folder is ignored. Uninstalling is deleting the zip.

Building from source instead puts the same file at `build/RNGTick.zip`. Either way the zip holds
exactly one top-level folder, named for the mod id, which is what the loader requires:

```
RNGTick.zip
  RNGTick/
    mod.json
    RNGTick.dll
    LICENSE
```

The mod ships no materials, reactions or translations -- one class and one postfix -- so there is
nothing to configure.

## Building and testing

```sh
./build.sh                    # mod, tests, conformance suite
./run-tests.sh                # this project's tests. About a minute. The everyday loop.
./run-tests.sh --retirement   # instead: is the game still broken? See below.
./run-tests.sh --all          # everything installed in the test root, no filter
```

Tests use the [TestHarness](https://github.com/sparr/atomcraft-mod-TestHarness) and run inside the
real game, headless. This project does not build the harness and does not read its sources, so
point it at a **release** rather than at a tree you are working in:

```sh
ATOMCRAFT_HARNESS=/path/to/testharness-release   # tooling: run-tests.sh, bootstrap.sh, lib/
ATOMCRAFT_HARNESS_ZIP=/path/to/TestHarness.zip   # the pinned mod, and the assembly to build against
```

Put both in `harness.conf`, which is gitignored; `harness.conf.example` has the details, and the
harness's own per-user config at `$XDG_CONFIG_HOME/atomcraft-test/config` is read too, so one pair
of lines there configures every project that consumes it. There is no default for either, and a
missing one is an error rather than a guess. The assembly is taken out of the same zip the game
loads, so "compiled against" and "ran against" are the same bytes by construction.

### The retirement suite

`test/RetirementTests.cs` is the one place that asserts the **unmodified game** is still broken.
Nothing else in the project does: every other bar is absolute -- a sampling floor, a share of the
arithmetic expectation -- and would pass unchanged against a game that fixed itself tomorrow.

**A failure there is good news.** It means the defect is gone and the part of this mod that
addressed it can be retired; each failure message says which part. That is a different question
from whether the mod is correct, so it is excluded from the default run and asked deliberately.

The split was forced by a real event. Before it, three tests were written as "vanilla is badly
spread, the mod fixes it", and the 2026-09-12 build falsified the first half of each. On buildid
25276035 the suite reads:

| | |
| --- | --- |
| `VanillaStillLocksPositionsOutOfAReactionProbability` | passes -- still broken, keep |
| `VanillaStillFreezesARareShippedReactionAfterOneCycle` | passes -- still broken, keep |
| `VanillaIsStillBadlySpreadOnAPercentageCheck` | **fails** -- fixed upstream |
| `VanillaStillLeansOnACoinFlip` | **fails** -- fixed upstream |
| `AVanillaPositionStillBehavesLikeJust256Samples` | **fails** -- the volume is balanced, not sampled |
| `TheTickAloneOnlyRelocatesAPercentageCheckBias` | **fails** -- no bias left to relocate |

Which is the case for retiring the cycle term and keeping the tick: everything still failing in
the game has a modulus that does not divide 256.

`--all` drops the filter and runs whatever is installed in the test root. Since this project no
longer builds the harness, that includes the harness's own suite only if you staged its test mod
there yourself.

Tests ask `Atomcraft.RNG` for rolls rather than reimplementing the lookup: a test that computed its
own expected values would pass against a mod that patched nothing. The same rule applies one level
up, to the game's consumers of the RNG. `test/ReactionTests.cs` and its conformance counterpart
place a shipped material and count what the simulation does to it rather than modelling the gate in
`BaseMaterial`; the condensation test used to model its gate, and went on passing for a build where
the game had stopped evaluating the expression it modelled.

That is the failure worth guarding. Both `Roll` overloads carry `AggressiveInlining`, and a
Harmony detour on such a method is defeated by any caller compiled with the original body pasted
in -- silently, with the patch still reporting itself installed. So every check that can go
through one of the game's own wrappers (`RollPct`, `RollFloat`, `RandomDeterministic`) does, and
compares the wrapper's answer against the patched `Roll` rather than against a number the test
computed.

**Running the harness's own suite is still worth doing before a release**, if you have its test
mod staged. Every test here is a region test that never starts a session, so the harness's session
tests exercise nothing in this mod -- but this project patches `RNG.Roll` underneath them, which
is how it broke one of them once, and still does: `OriginalTests.TheTwoAgreeOnceThePatchIsGone`
asserts that an unpatched `RNG.Roll` matches its bound original, which cannot hold while any mod
is patching that method. Reported upstream.

## The conformance suite

`conformance/` is a separate mod that names none of this one: no project reference, no mention in
its `mod.json`, which depends only on the harness. Install it beside any candidate fix and it
reaches a verdict; install it alone and it fails, because stock Atomcraft does not pass.

Its assertions are **absolute** rather than diffs against stock, so it stays valid against a
version of the game that fixes this itself -- there is no stock to compare against once the stock
is the thing being judged. A fair generator still leaves a spread of `sqrt(p(1-p)/n)` between
positions and no fix can beat that, so each test asks for a spread within three times that floor.
Measured against buildid 25276035:

| | stock | with this mod |
| --- | --- | --- |
| reaction at `Probability 240` | 10.8x the floor, 44 of 144 positions dead | 1.0x, none dead |
| `RollPct` at 5 in 128 | 0.0x, every position exactly 0.03906 | 1.0x |
| coin flip | 0.0x, every position exactly 0.50000 | 0.8x |
| `Compacted Dirt Decomposition`, in the simulation | 411 of 1922 sites, frozen after 256 ticks | 1775 (92.4%) |

A spread of exactly zero is not a well-behaved generator. The 2026-09-12 build fills the RNG volume
with a balanced permutation of 0-255 per position, so **every power-of-two consumer is now exactly
uniform by construction** -- and a rare event, whose modulus does not divide 256, is untouched. The
suite's power-of-two tests pass on stock today and its rare-event tests do not.

`RollPct` is still tested separately on purpose: 128 divides 256, so a fix that only adds the tick
passes the reaction test and fails that one.

The last row is the one that does not model anything. Every other test reduces `RNG.Roll` the way a
consumer would -- `r % 240 == 0` for the reaction gate -- which is a copy of the game's code with
no link back to it, and such a copy went stale once already in this repository without anything
failing. `conformance/ShippedReactionTests.cs` builds 1922 one-pixel sites of a shipped material,
lets the game's own `Step` run for ten cycles, and counts. Stock fails it by freezing: 411 sites
react inside the first 256 ticks and not one more in the 2304 that follow.

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

Seven tests. Against buildid 25276035 stock, three fail: the reaction spread, the in-simulation
reaction, and nothing reaching `RNG.Roll`. The four that pass are the coin flip and the percentage
check, which the balanced volume made exactly uniform, and determinism and range, which stock never
got wrong.

## A note on the game, found along the way

`RollLowerThanChanceOutOf1024_TimeOnly` is
`return (RNGTimeOnly[tick & 0xFFF] &= 1023) < chance;` -- a compound assignment, confirmed in IL.
Every call writes back into the table. Masking is idempotent so the answers never change, but a
method the simulation calls per pixel, from inside `Parallel.ForEach` over chunks, writes to a
shared array on every call.
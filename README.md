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

Postfixes both `RNG.Roll` overloads, offsets the result by the tick plus a hash of the 256-tick
cycle, and wraps it back into `[0, int.MaxValue)` -- the range `Random.Next()` promises and every
caller was written against. That last part is not cosmetic: `RandomDeterministic` uses a roll as
an array index.

The cycle hash is there because if the modulus divides 256 then `tick % m` is already decided by
the `tick & 255` that chose the value, and the tick alone would change nothing. That is not a
corner case -- `RNG.RollPct` is `Roll(..) & 0x7F` and is the most-used roll in the game. The hash
is the game's own, from `Simulation.PRNG`.

## Install

Needs [GodotMonoModLoader](https://github.com/sacroimper/GodotMonoModLoader). Drop
`build/RNGTick.zip` into `<user data>/Mods/`, still zipped. The mod ships no materials, reactions
or translations -- one class and one postfix -- so there is nothing to configure and nothing to
uninstall beyond deleting the zip.
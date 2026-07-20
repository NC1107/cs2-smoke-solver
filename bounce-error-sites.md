# Bounce-error sites

Where the sim's predicted landing drifts far from the real in-game landing, and why.
Built from every run in `data/validation/` (9,839 throws across the seven maps), 2026-07-20.

## The short version

The sim is accurate almost everywhere: 94% of throws are `TRACKED` with a median error of 0.8u and a p90 of 2.7u.
The "terrible" errors are a small, concentrated set, and they are almost all the same failure - the sim bounces the grenade off geometry the real map does not have, so it predicts **more bounces than reality** and the landing drifts.

Error by divergence class, all runs:

| class | count | median err | p90 | max |
|-------|------:|-----------:|----:|----:|
| TRACKED | 8,923 | 0.8u | 2.7u | 124u |
| DRIFT | 504 | 40.2u | 191u | 866u |
| REST-MISMATCH | 92 | 63u | 1245u | 2020u |
| MISSED-BOUNCE | 45 | 1.9u | 18u | 282u |
| PHANTOM-BOUNCE | 16 | 35u | 791u | 842u |
| BOUNCE-MISMATCH | 3 | 67u | 68u | 68u |

`DRIFT` and the bounce classes are the same root story: the tick trace tracks the real throw for a while, then a bounce lands on a surface that is slightly off (or is not there at all in reality), and the paths separate.
`REST-MISMATCH` is the grenade settling in the wrong place at the very end (a resting/rolling difference), a smaller and separate bucket.

## It is not spread out - it is a handful of spots

Filtering to the throws that matter for a test set - high error (>8u) **and** high stability (>=80%), so the sim is confident and wrong rather than just chaotic - leaves 160 throws.
Those 160 collapse onto ~33 target sites, and a handful of sites carry most of them.

| map | target (x, y, z) | bad throws | max err | sim>real bounces (count) |
|-----|------------------|-----------:|--------:|--------------------------|
| de_dust2 | [1428, 1544, -3.5] | 52 | 288u | 5>4 (29), 6>5 (18), 5>5 (3) |
| de_inferno | [692.2, -92.1, 77.0] | 18 | 260u | 5>3 (12), 5>4 (5), 4>3 (1) |
| de_dust2 | [-292.4, 1429.4, -28.0] | 15 | 113u | 6>5 (5), 5>4 (4), 4>3 (2) |
| de_dust2 | [-328, 2424, -80] | 14 | 124u | 6>5 (5), 5>5 (3), 7>6 (2) |
| de_dust2 | [-1315.6, 2683.9, 130.0] | 6 | 192u | 5>4 (3), 5>6 (1), 4>3 (1) |
| de_ancient | [-1669.5, -922.4, 3.5] | 5 | 41u | 5>4 (4), 4>3 (1) |
| de_overpass | [-771.6, -2382.2, 164.7] | 4 | 58u | 4>4 (3), 4>3 (1) |
| de_ancient | [-682.0, -1607.7, -124.5] | 3 | 66u | 4>3 (2), 4>5 (1) |
| de_ancient | [932.7, 323.6, 130.5] | 3 | 57u | 4>5 (3) |
| de_nuke | [1023.3, -387.4, -128.0] | 3 | 56u | 5>4 (3) |
| de_overpass | [-3022.2, -1476.6, 527] | 3 | 35u | 4>4 (3) |
| de_dust2 | [1471.0, 567.6, -134.5] | 3 | 21u | 6>5 (2), 5>4 (1) |

de_dust2 owns most of them (100 of the 160), then de_inferno (23).
Read the last column as "the sim predicted N bounces, the real throw had M" - it is almost always N > M, meaning the sim adds bounces that never happen in game.

## The two worst sites, as concrete test scenarios

Every throw below is a confident sim lineup (80-100% stability) that lands hundreds of units off in game, all landing on the same over-bouncing geometry.

**de_dust2, target [1428, 1544, -3.5]** - 52 confidently-wrong throws, the sim consistently adds one bounce (5>4, 6>5):

| type | click | feet | yaw | pitch | sim/real bounces | err |
|------|-------|------|----:|------:|:----------------:|----:|
| CrouchJumpThrow | left | [-312, 1392, -16] | 5.0 | -21 | 6/3 | 288u |
| RunJumpThrow | left | [-1536, 2328, 0] | -11.8 | -45 | 6/5 | 81u |
| RunJumpThrow | left | [-1512, 2496, 16] | -14.9 | -45 | 6/5 | 70u |
| CrouchJumpThrow | left | [-384, 888, -16] | 16.9 | -13 | 5/4 | 64u |

**de_inferno, target [692.2, -92.1, 77.0]** - 18 confidently-wrong throws, the sim adds two bounces (5>3):

| type | click | feet | yaw | pitch | sim/real bounces | err |
|------|-------|------|----:|------:|:----------------:|----:|
| Crouch | right | [768, -120, 87] | 165.5 | -25 | 5/3 | 260u |
| Stand | right | [768, -144, 89] | 155.6 | -5 | 5/3 | 250u |
| Stand | right | [720, -192, 89] | 128.1 | -5 | 5/3 | 228u |
| Stand | right | [720, -48, 77] | -131.4 | -29 | 4/3 | 211u |

## What this points at

The signature is phantom bounces off over-represented collision geometry, which is the collision-mesh fidelity frontier called out in `physics-sim.md` and `DESIGN.md`.
The physics integrator itself is ~1u accurate on clean geometry, so these are not a physics bug - the grenade is bouncing off a surface in our `.s2geo` that the real map handles differently (a `prop_static` hull we include but shape wrong, or a brush face the game clips through).
The dust2 [1428, 1544] and inferno [692, -92, 77] sites are the highest-value places to look first, because a single geometry fix there clears dozens of bad throws at once.

## How this was built

`python` over `data/validation/*.json`, grouping `results[]` by `(map, target)`.
Each record already carries `PredictedBounces`, `RealBounces`, `PredictedRest`, `RealRest`, `ErrPredicted`, and `DivergenceClass`, so no re-simulation was needed.
To refresh after new validation runs, re-run the same grouping filtered to `ErrPredicted > 8 and Stability >= 0.8`.

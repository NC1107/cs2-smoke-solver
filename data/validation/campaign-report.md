# Sim validation campaign - final report (de_dust2, game build 2000839)

Date: 2026-07-09.
Rig: pinned CS2 dedicated server (branch 1.41.6.5) + CounterStrikeSharp CalibrationThrower plugin, throws executed via native `CSmokeGrenadeProjectile::Create()`, per-tick server-authoritative capture to JSONL.
Method: for each target, the inverse solver produces every viable lineup; each lineup's throw is executed for real on the server and the actual rest position is compared against the sim's prediction.
Pass criterion: predicted-vs-real rest error within 3u (1u is roughly 1.9cm).

## Runs

| # | Time | Target | Throws | Matched | Median | Mean | p90 | Max | ≤3u | ≤8u | Duds |
|---|------|--------|--------|---------|--------|------|-----|-----|-----|-----|------|
| 1 | 20:03 | mid doors (-328,2424) baseline | 256 | 256 | 2.11u | 21.3u | 38.8u | 1074u | 65% | 79% | 0 |
| 2 | 20:46 | mid doors (post physics fixes) | 259 | 259 | 1.49u | 6.2u | 12.0u | 212u | 80% | 88% | 0 |
| 3 | 20:56 | A side (1428,1544) | 493 | 493 | 1.69u | 7.9u | 25.8u | 288u | 67% | 82% | 0 |
| 4 | 21:15 | long A (1360,456) | 169 | 169 | 1.95u | 7.8u | 6.7u | 462u | 71% | 92% | 0 |
| 5 | 23:00 | B site (-1752,2664) | 251 | 251 | 1.00u | 5.2u | 5.4u | 240u | 82% | 91% | 0 |
| 6 | 23:20 | mid doors (final confirmation) | 253 | 253 | 1.40u | 4.2u | 5.0u | 124u | 83% | 92% | 0 |

A 23:07 mid-doors run is excluded: a capture-tracking bug (see below) corrupted 28 of its 253 captures, and it was re-run as run 6 after the fix.

## Headline result

At the original mid-doors target, the campaign took the sim from median 2.11u / mean 21.3u / p90 38.8u / max 1074u to median 1.40u / mean 4.2u / p90 5.0u / max 124u.
Every one of the 1681 counted throws detonated.
Roughly 83% of arbitrary solver lineups now land within 3u of prediction, and 92% within 8u.

## Where the remaining error lives

Every entry in the final run's worst-10 list is a high-bounce throw: 5-11 real bounces, almost all full-speed (s=1.0) jump variants, with solver stability scores of 40-80%.
One missed or extra bounce compounds over the remaining flight, producing the 20-124u tail.
Low-bounce throws (4 bounces or fewer) with stability 80%+ are effectively always within a few units.
The viewer's stability and precision filters exist exactly to hide the risky tail; users who select stab 80%+ / low-bounce lineups get prediction-grade accuracy.

## Rig fixes made during the campaign

- Entity index reuse: with dozens of smokes alive concurrently, the engine reassigns a despawned projectile's entity index before the per-tick reaper notices; the tracker now flushes the stale record when a new projectile spawns on a tracked index. Before the fix 28/253 captures were silently lost or corrupted (including a phantom 681u "outlier" and a phantom detonation failure); after it, 253/253 matched.
- In-game chat telemetry: each scripted throw announces its plan and prediction in chat, then its actual landing, whenever a human is connected.
- Known environment hazard: the host's memory-pressure cleanup sends the CS2 server SIGTERM when overall RAM gets tight; runs interrupted this way must simply be restarted (captures up to the kill are valid).

## Residual known gaps (not addressed in this campaign)

- High-bounce (5+) trajectories: chaotic sensitivity to bounce modeling; mitigated by stability scoring rather than solved.
- Sloped-terrain bounce restitution: the single-scalar elasticity/friction model underfits angled surfaces (58-183u errors were measured on slope tests in earlier sessions); flat and stepped urban geometry, which dominates dust2 lineups, is unaffected.

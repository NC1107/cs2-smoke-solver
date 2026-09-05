# Technical Debt

Generated 2026-07-10 by a four-specialist read-only audit (C# core, CounterStrikeSharp plugin, web viewer, rig tooling).
No code was changed; every item below is a finding with a proposed fix, ranked and batched for later work.

Regions: **core** = src/ (Sim, Solver, Cli, Extraction), **plugin** = rig/CalibrationThrower, **viewer** = viewer/index.html, **tooling** = rig scripts + ops.

## Summary

| Severity | Count |
|---|---|
| Critical | 2 |
| High | 27 |
| Medium | 45 |
| Low | 24 |
| **Total** | **98** |

Themes at audit time: the physics core was strong but wrapped in fragile I/O boundaries; the three load-bearing rig mechanisms (signature scan, meta queue, file IPC) all failed silently; nothing long-lived was supervised; the viewer's 3D lifecycle leaked; accessibility was the weakest UI axis; the exact-physics stack had zero test coverage.
**Status: all seven batches completed 2026-07-10 (see Progress). 95+ of 98 findings resolved; partials and descopes are noted honestly in the batch entries.**

## Progress

- **Batch 1 - completed 2026-07-10.**
  Fixed: C1, H24, L24 (gitignore + baseline commit + scratch cleanup), H12, H17, M17, M35 (atomic rename IPC end to end, shared calibipc.py), H20, H21, H22, M18 (watcher claim-by-rename, stale discard, solver-error distinction, stderr to rig.log, malformed-request quarantine), H13 (offset tailing with rotation guard), H23 (50 MB capture rotation), H4 (async capture writer off the tick thread), H5 (AddTimer everywhere, slot-safe callbacks), M16 (Unload flushes captures and beams), M22 (marker/array validation), M34 (summary from JSON report), plus a watcher heartbeat (C2 groundwork).
  Verified: build + 18/18 tests, live end-to-end throw through the new protocol (capture persisted by the writer thread, legacy 317 MB file auto-rotated), chat relay, heartbeat.

- **Batch 2 - completed 2026-07-10.**
  Fixed: H2 (signature self-test at load/map start; synthetic throws disable loudly on failure), H3 (meta correlated to spawns by initial pos/vel epsilon; registered only after a successful native create), H1 (command allowlist on the request channel), H14 (z-cap relative to target), H15 (cache key includes tolerance/originReach/attrs, version bumped), H16 (API body validation with 400s and a size cap), H25, H26, H27 (viewer boot/mesh error paths and 3D init re-entrancy), M6 (empty-zone guard plus diagnostic), M7 (process-wide invariant culture), M19 (invariant chat-command parsing), M20 (map identity in every request, per-map markers, map-aware watcher), M39 (top-level CLI error handling).
  Verified: builds, 18/18 tests, live throw after self-test (synthetics enabled), `{"cmd":"quit"}` rejected by allowlist with server alive, API returns clean 400s for malformed/out-of-bounds/non-finite bodies.

- **Batch 3 - completed 2026-07-10.**
  Fixed: C2 (three systemd user units with Restart=on-failure and journald logging; watcher heartbeat), C1 follow-through (74 GB install relocated to ~/cs2-rig), M31 (published CLI binary in rig/bin used by the watcher and units), M32/M33 (bash-array CLI, set -u -o pipefail, per-request functions - landed with the batch-1 watcher rewrite), M36/L15 (paths derived from script location / rig.env; no hardcoded user paths in scripts), M37 (rig/deploy-plugin.sh: build, atomic file-set copy, cfg sync, hot reload), M38 (journald + shared rig.log via calibipc logging), M21 (per-player !goto state keyed by slot, cleared on map start).
  Verified: all three units active under systemd, plugin loaded on the relocated server, fresh watcher heartbeat, end-to-end throw captured through the new paths.

- **Batch 4 - completed 2026-07-10.**
  Fixed: H9 (ThrowSpec/TrajectoryResult are readonly record structs), H10 (pointlineup coarse sweep and refinement parallelized: 6.2s -> 1.4s on the reference solve, identical answer), H11 (serve survives port conflicts and shuts down on ctrl-c), H18 (shared marker geometry/materials, disposal for owned geometries), M1 partial (BestLineup and validate reuse the solve's collider; full cross-query reuse lands with the batch-5 server rework), M2 (flood fill reuses the visited set as CellSet), M3 (collider buckets flattened to CSR arrays), M4 (Amanatides-Woo DDA for long rays with early-out; short sweeps keep the AABB fast path), M5 (parallel voxelization with interlocked bit sets), M23 (tick-loop scratch collections reused), M24 (three.js lazy-loads only when 3D opens; dead GLTFLoader deleted), M25 (done in batch 2), M26 (stamped lineup indices, shared nearest-marker helper), M40 (static files streamed), L18 (single scheduleDraw), L19 (reused fly() vectors), L10 (re-mark replaces beam; round restarts redraw), L11 (smoke-visibility toggle persisted).
  Verified: 18/18 tests, pointlineup reference answer bit-identical after the collider rewrite, live throw pipeline after plugin redeploy, viewer serves with zero blocking script tags.

- **Batch 5 - completed 2026-07-10.**
  Fixed: H7 (Program.cs 2,182 -> 48 lines: 15 command classes + CliParsing/MeshSetup/LineupApi/TargetSolver services + Types.cs, using-static cross-imports, zero behavior change verified by tests and a bit-identical reference solve), H8 + H11 + M30 + M40 + L4 (serve rewritten on ASP.NET Core minimal API: localhost-only Kestrel, async handlers, SemaphoreSlim(2) solve gate, ETag/304 on mesh and data, cache headers, hardened path guard, friendly port-conflict error, graceful SIGINT, MeshPayloadCache static deleted, body cap enforced while reading), M29 (viewer split into app.css + six ES modules with acyclic imports, browser-verified E2E), M8 (Simulate derives launch state via DeriveInitial), M9 (shared ParseVec2or3), M41 (direct pinned SkiaSharp reference), L12/L13/L14 (shared Reply helper, ChatColors throughout, named constants - including correcting a factually wrong team comment rather than changing behavior).
  Descoped from M11: the validate polling loop keeps its deliberate blocking cadence (it is a batch CLI); the protocol classes are centralized in Types.cs.
  Verified: solution build, 18/18 tests, publish, plugin redeploy + live throw, new server serving the module viewer with correct MIME types, API 400s, traversal blocked, mesh ETag.

- **Batch 7 - completed 2026-07-10.**
  Fixed: H6 (TriangleCollider SAT/DDA tests incl. the graze and start-touching cases; exact-integrator tests; golden replays streaming 5 real captures against real dust2 geometry, non-vacuous at ~1.9u), M43 (initially partial; closed 2026-07-13 on branch test/lineupsolver-coverage: 21 tests over Solve/VerifyExact/OriginsFromNavAreas/NavGroundZ with mutation-verified assertions for the ordering comparator, bucket tie-break, snap-to-ground, and bounds guards), L8 (AttributeInteractAs round-trip, GrenadeSolidFilter semantics + end-to-end, Occlusion DDA edges), L9 (rcon.py and rcon_password deleted), M42 (vendored GLTFLoader deleted), L6 (usage generated from the command table), L2 (LoadJson<T> at six sites), L3 (ExportGltf disposables), L5 (BaseGravity/FloorNormalZ/ContactBackoff constants), L7 (extraction model lookup dictionary), M44 (handled in the batch-6 viewer pass).
  NEW finding from the test agent, fixed: the voxel stage-1 simulator lacked the exact path's floor-impact damp gate, overshooting ~120u on fast steep throws and starving VerifyExact of candidates; the damp is now applied in both integrators. Post-fix live validation: 40/40 captured, median 0.8u, 98% within 8u (better than the pre-fix baseline).
  Tests: 18 -> 40, all green, ~1.6s wall.

- **Batch 6 - completed 2026-07-10.**
  Fixed: H19 (keyboard-navigable lineup list with roving tabindex driving the same selection path as canvas markers), M28 (aria-live status; solve overlay is a proper dialog with focus management), M15 (labeled selects with units in options; light-theme muted text at 5.55:1 / 6.15:1), M14 + L23 (colorblind-safe heatmap: blue fill vs orange outline-only, dual-channel, folded into the map key), M27 (controls anchored in the stage; sub-640px collapse to details, full-width panel), M12 (AbortController + Cancel button on the solve overlay), M13 (WASD keys bound only while 3D is live, cleared on blur/stop, UI-target guard), M45 (theme changes recolor the 3D scene; DPR changes re-apply on monitor moves), L17 (tooltip clamped), L21 (one .btn base class), L20 (esc() for API-derived strings), L16 (dead CSS removed; lineup card click wired to deselect), M44 (named viewer constants).
  Verified live in a browser including the accessibility tree: keyboard selection, mid-flight cancel with focus restoration, colorblind heatmap swatches, responsive collapse at 500px, theme flip recoloring 2D and 3D, zero console errors.
  Note: most batch-6 file changes were swept into the batch-7 commit by parallel timing; this commit adds the final straggler and this note.

## Work batches

Do them roughly in order; batch 1 prevents catastrophes, batch 2 eliminates silent lies, the rest are quality-of-life in priority order.

| Batch | Theme | Findings | Est. effort |
|---|---|---|---|
| 1 | Stop data loss and repo catastrophe | C1, C2, H12, H13, H16, H17, H20, H23, H24, M16, M17 | ~2 days |
| 2 | Eliminate silent failures | H2, H3, H14, H15, H21, H22, H25, H26, H27, M6, M7, M18, M19, M20, M39 | ~2-3 days |
| 3 | Ops hardening | M31, M32, M33, M34, M35, M36, M37, M38, M21, M22 | ~2 days |
| 4 | Performance | H9, H10, H11, H18, M1, M2, M3, M4, M5, M23, M24, M25, M26, L10, L11, L18, L19 | ~3 days |
| 5 | Architecture and structure | H7, H8, M8, M9, M10, M11, M40, M41, M42, L12, L13, L14, L15, L20, L21 | ~1-2 weeks |
| 6 | UX and accessibility | H19, M12, M13, M14, M15, M27, M28, M29, M30, L16, L17, L22, L23 | ~3 days |
| 7 | Testing and hygiene | H6, M43, M44, M45, L1..L9, L24 | ~3 days |

---

## Critical

### C1. 74 GB game install is one `git add .` away from being staged
- Where: `.gitignore:1` (tooling)
- Category: hygiene · Effort: small
- `rig/server/` (74 GB) and `rig/steamcmd/` (202 MB) live inside the repo, untracked but NOT ignored.
- A single `git add .` stages the entire game install; git also stats the tree on every status.
- Fix: ignore `rig/server/` and `rig/steamcmd/`; longer term move the install out of the repo and reference it via one config value.

### C2. No supervision for any long-lived rig process
- Where: `rig/watcher.sh:10` (tooling)
- Category: ops · Effort: medium
- The CS2 server, the viewer serve process, and watcher.sh are all started by hand with no restart policy, health check, or journal.
- All three were found dead at audit time with nothing noticing; in-game `!test`/`!lineup` hang silently when the watcher is down.
- Fix: three systemd user units with `Restart=on-failure` and journald logging, plus a watcher heartbeat file the plugin can check and warn about in chat.

## High

### H1. `cmd` channel executes arbitrary server console commands
- Where: `rig/CalibrationThrower/CalibrationThrowerPlugin.cs:151` (plugin)
- Category: security · Effort: small
- Any process running as the user can write `request.json` with `{"cmd": ...}` and get an unauthenticated server console.
- Fix: replace with typed request kinds or a strict command allowlist; tighten the calib dir to 0700; log rejected commands.

### H2. Native signature scan has no runtime verification
- Where: `rig/CalibrationThrower/CalibrationThrowerPlugin.cs:62` (plugin)
- Category: robustness · Effort: medium
- The byte signature is build-specific and has already silently bound to the flashbang Create once; throws "succeed" but no smoke spawns and captures silently stop.
- Fix: self-test on map start (throw once, assert a `smokegrenade_projectile` spawn within N ticks), loud error plus a disabled flag on failure.

### H3. `_pendingMeta` leaks on failed synthetic create; player smokes can be mislabeled and deleted
- Where: `rig/CalibrationThrower/CalibrationThrowerPlugin.cs:209` (plugin)
- Category: correctness · Effort: medium
- Meta is enqueued before the native call; if the call fails the entry is never dequeued and the next spawn (possibly a real player throw) steals it, gets a wrong `predict`, and is deleted at bloom.
- Attribution is FIFO, so a human throwing mid-batch steals a synthetic's meta even with no failure.
- Fix: correlate meta to spawns by matching InitialPosition/InitialVelocity within an epsilon; dequeue-on-failure as the minimum fix.

### H4. Per-capture synchronous ~74 KB writes on the server tick thread
- Where: `rig/CalibrationThrower/CalibrationThrowerPlugin.cs:362` (plugin)
- Category: performance · Effort: medium
- FlushRecord serializes and appends the full tick trace inside OnTick; measured average 74 KB per record, and `!clearsmokes` can flush dozens in one tick.
- Fix: copy records into a ConcurrentQueue drained by a background writer holding one long-lived StreamWriter.

### H5. Timers bypass AddTimer and survive plugin unload
- Where: `rig/CalibrationThrower/CalibrationThrowerPlugin.cs:86` (plugin)
- Category: correctness · Effort: small
- All five `new Timer(...)` usages are untracked, so pending callbacks fire into the unloaded plugin instance on hot reload; this is a known crash vector and this rig hot-reloads constantly.
- Fix: use `AddTimer` everywhere; re-resolve players by slot inside callbacks.

### H6. Exact physics stack has zero test coverage
- Where: `src/Sim/GrenadeTrajectory.cs:233` (core)
- Category: testing · Effort: medium
- `SimulateExactRaw`, `FirstHitHull`, and the swept-box SAT decide every shipped answer; none has a single test, and the comments document past deadlock/ping-pong bugs in exactly these paths.
- Fix: synthetic-mesh tests for TOI, bounce reflection, rest rule, ramp deflection, graze case, plus golden-trajectory replays of captured real throws.

### H7. Program.cs is a 1,983-line monolith mixing 15 concerns
- Where: `src/Cli/Program.cs:1` (core)
- Category: architecture · Effort: large
- CLI parsing, HTTP server, JSON API, radar renderer, calibration fitting, rig RPC, glTF export, and report generation all live in one file of file-local statics; nothing is unit-testable and shared logic has already drifted (see H14).
- Fix: one class per command plus extracted shared services; consider System.CommandLine.

### H8. HTTP server is synchronous and single-threaded
- Where: `src/Cli/Program.cs:740` (core)
- Category: architecture · Effort: medium
- One map-wide solve blocks every other request for minutes; no async I/O anywhere in the server path.
- Fix: `GetContextAsync` + `Task.Run` per request with a solve-concurrency semaphore, or an ASP.NET Core minimal API.

### H9. Hot solver loops allocate record classes per simulation
- Where: `src/Sim/GrenadeTrajectory.cs:20` (core)
- Category: performance · Effort: small
- ThrowSpec and TrajectoryResult are heap records allocated millions of times per solve inside Parallel.ForEach.
- Fix: make both `readonly record struct`; verify gen0 rate with dotnet-counters.

### H10. pointlineup runs ~100k simulations single-threaded
- Where: `src/Cli/Program.cs:1436` (core)
- Category: performance · Effort: small
- The coarse sweep and refinement are embarrassingly parallel over a read-only collider but run on one core, directly inflating in-game `!plineup` latency.
- Fix: flatten combinations and Parallel.ForEach with thread-local bests.

### H11. serve crashes with an unhandled exception on port conflict and has no shutdown path
- Where: `src/Cli/Program.cs:735` (core)
- Category: correctness · Effort: small
- `listener.Start()` is unguarded (this crash was observed live); no Ctrl-C handling, listener never disposed.
- Fix: catch HttpListenerException with a friendly message; CancelKeyPress handler; `using` the listener.

### H12. request.json handoff is a non-atomic write race (CLI side)
- Where: `src/Cli/Program.cs:1650` (core)
- Category: correctness · Effort: small
- `File.WriteAllText` fills the file in place while the plugin polls every 8 ticks; a partial read drops or corrupts a batch, and deletion-as-ack makes it look consumed.
- Fix: write to a temp file and `File.Move` (atomic rename); same pattern everywhere in the protocol.

### H13. captures.jsonl polling can read a partial line and kill a live run
- Where: `src/Cli/Program.cs:1697` (core)
- Category: correctness · Effort: small
- ReadAllLines during plugin appends can yield a truncated last line; the JsonException is unhandled and aborts a run with hundreds of throws in flight; full re-read every 2s is also O(n²).
- Fix: persistent stream at last-consumed offset, only parse newline-terminated lines, defer partial lines.

### H14. SolveForTarget hard-caps the solve grid at absolute z=900
- Where: `src/Cli/Program.cs:967` (core)
- Category: correctness · Effort: small
- Unlike the relative cap in Lineups, the shared path behind the API/bestlineup/validate excludes all playable space on high maps (de_vertigo ~z=11700) and returns zero lineups with no error.
- Fix: compute the cap relative to target z; factor region math into one shared helper.

### H15. /api/lineup cache key omits tolerance, originReach, and attribute filter
- Where: `src/Cli/Program.cs:888` (core)
- Category: correctness · Effort: small
- Queries differing only in those parameters replay the first cached answer; a cache surviving a restart with different `--attrs` serves stale results permanently.
- Fix: include all inputs in the seed and bump QueryVersion.

### H16. /api/lineup accepts arbitrary unvalidated POST bodies
- Where: `src/Cli/Program.cs:900` (core)
- Category: correctness · Effort: medium
- Missing keys → 500s; NaN/Infinity/absurd coordinates flow into minutes-long solves and unbounded cache-dir growth; no body size limit.
- Fix: validate shape and ranges, clamp parameters, cap body size, return 400s.

### H17. Relay channel drops messages on timeout and writes non-atomically
- Where: `rig/relay-chat.py:9` (tooling)
- Category: robustness · Effort: medium
- All relays spin-wait 10s then overwrite `request.json` unconditionally (last-writer-wins); relay-plineup sends two payloads back-to-back so a slow plugin loses the first; exists-then-write is also a TOCTOU.
- Fix: atomic rename writes, timeout-as-error, or a queue directory of uniquely named files.

### H18. Marker/target three.js resources never disposed on rebuild
- Where: `viewer/index.html:953` (viewer)
- Category: performance · Effort: small
- Every sync3d allocates fresh geometries/materials and `.remove()` never frees GPU resources; VRAM grows on every filter change and can eventually lose the WebGL context.
- Fix: dispose on removal, or share one sphere geometry and three materials created at init.

### H19. Core marker interactions are mouse-only
- Where: `viewer/index.html:744` (viewer)
- Category: accessibility · Effort: medium
- Lineups can only be inspected via canvas hit-tests; no keyboard path or AT semantics exists for the app's primary function (WCAG 2.1.1 failure).
- Fix: render filtered lineups as a keyboard-navigable list in the panel driving the same `select(i)` path.

### H20. Watcher deletes request files before validating them and replays stale requests on restart
- Where: `rig/watcher.sh:12` (tooling)
- Category: robustness · Effort: small
- `cat` then `rm -f` loses a mid-write request permanently; requests written while the watcher is down execute unprompted on restart, possibly hours later.
- Fix: claim by rename, delete only after successful parse; timestamp requests and discard stale ones at startup.

### H21. Solver crashes are reported to the player as "no lineups found"
- Where: `rig/relay-lineup.py:9` (tooling)
- Category: standards · Effort: small
- Bare `except Exception` maps any CLI crash to the same payload as a legitimate negative; a broken build becomes indistinguishable from a correct empty answer.
- Fix: catch JSONDecodeError only, log raw argv, emit a distinct "solver error" chat line.

### H22. CLI stderr discarded on lineup requests
- Where: `rig/watcher.sh:34` (tooling)
- Category: standards · Effort: small
- `2>/dev/null | tail -1` throws away compile errors and stack traces, feeding empty RESULT into the false-negative path above.
- Fix: redirect stderr to a watcher log; check exit status and RESULT emptiness before relaying.

### H23. captures.jsonl grows unbounded (317 MB in one day)
- Where: `rig/CalibrationThrower/CalibrationThrowerPlugin.cs:362` (tooling/plugin)
- Category: ops · Effort: small
- Full per-tick telemetry per throw, no rotation/compression; downstream reads slow linearly and the CLI baseline re-read makes campaigns quadratic.
- Fix: roll per validation run or size-rotate at ~50 MB with gzip; retention policy.

### H24. Blanket `data/` ignore leaves hand-made calibration ground truth unversioned
- Where: `.gitignore:3` (tooling)
- Category: hygiene · Effort: small
- markers.json, throws.json, throw-constants.json, and validation reports are irreplaceable human-labor data with no version control, while only regenerable artifacts need ignoring.
- Fix: narrow the ignore to regenerable files; track the precious ones.

### H25. 3D mesh fetch has no error handling; failure wedges the 3D toggle
- Where: `viewer/index.html:812` (viewer)
- Category: correctness · Effort: small
- No `res.ok` check; a 404 empty body throws in the binary parser, the rejection is unhandled, and the user is left on an empty stage.
- Fix: check ok, wrap the toggle in try/catch that restores 2D and explains the failure.

### H26. Startup fetch and radar image load have no error paths
- Where: `viewer/index.html:371` (viewer)
- Category: correctness · Effort: small
- Missing data file → unhandled rejection and a blank app; image 404 hangs the boot promise forever with no visible message.
- Fix: try/catch boot, `radar.onerror`, render a visible error state naming the missing file.

### H27. ensure3d is not re-entrancy safe
- Where: `viewer/index.html:808` (viewer)
- Category: correctness · Effort: small
- Rapid 3D toggling during the initial fetch creates duplicate renderers, controls, and permanent window listeners; the first GL context leaks forever.
- Fix: memoize the in-flight init promise; re-check toggle state after the await.

## Medium

### M1. VoxelGrid and TriangleCollider rebuilt from scratch per query
- Where: `src/Cli/Program.cs:968` (core)
- Category: performance · Effort: medium
- Every API query and validate run rebuilds both structures; BestLineup builds a second identical collider over the same region.
- Fix: build once at serve startup (or LRU by region); return the collider from SolveForTarget for reuse.

### M2. SmokeFloodFill allocates five collections per fill inside the parallel loop
- Where: `src/Sim/SmokeFloodFill.cs:65` (core)
- Category: performance · Effort: small
- Per-candidate HashSet/List/Queue plus a fully redundant second HashSet (`CellSet = [.. cells]` rebuilds `visited`).
- Fix: pass `visited` as CellSet; pool per-thread buffers.

### M3. TriangleCollider buckets are jagged List<int>[] with poor locality
- Where: `src/Sim/TriangleCollider.cs:15` (core)
- Category: performance · Effort: small
- The innermost loop of the whole system chases object references per cell.
- Fix: flatten to CSR (prefix-sum `cellStart` + contiguous `triangleIds`) after build.

### M4. Ray queries scan the segment's full AABB instead of DDA cells
- Where: `src/Sim/TriangleCollider.cs:319` (core)
- Category: performance · Effort: medium
- A 1200u aim ray visits ~1000 box cells instead of ~30 line cells, with no early-out ordering.
- Fix: Amanatides-Woo DDA (Occlusion.cs already has one to model after) with early exit on bestT.

### M5. VoxelGrid.Build voxelizes single-threaded
- Where: `src/Sim/VoxelGrid.cs:100` (core)
- Category: performance · Effort: small
- Dominates command startup while everything downstream is parallel.
- Fix: Parallel.For with per-thread masks OR-merged at the end (or Interlocked.Or).

### M6. Empty landing zone causes NaN centroid and a wasted full sweep
- Where: `src/Solver/LineupSolver.cs:55` (core)
- Category: correctness · Effort: small
- Division by zero when zoneCrossings is empty; NaN defeats every range prune, so the solver burns minutes to return nothing with no diagnostic.
- Fix: return empty immediately with a logged "target has no reachable landing cells".

### M7. Culture-sensitive formatted output corrupts machine-consumed strings
- Where: `src/Cli/Program.cs:458` (core)
- Category: correctness · Effort: small
- setpos/setang console strings, the Ground TSV, and report numbers use the current culture; comma-decimal locales produce `setang 12,5`.
- Fix: set invariant culture process-wide at startup (covers Parallel threads).

### M8. Launch-state derivation duplicated between Simulate and DeriveInitial
- Where: `src/Sim/GrenadeTrajectory.cs:101` (core)
- Category: standards · Effort: small
- The calibrated pitch-bias/speed/jump math is byte-for-byte duplicated; a fix to one copy silently desynchronizes the two simulators.
- Fix: Simulate calls DeriveInitial and integrates from its result.

### M9. Target parsing and click naming duplicated across five subcommands
- Where: `src/Cli/Program.cs:1345` (core)
- Category: standards · Effort: small
- The x,y[,z] parse is hand-rolled three times; the click-name ternary appears four times; the lineup JSON shape twice.
- Fix: ParseVec2or3 helper, one ClickName, one lineup DTO.

### M10. Static file path guard bypassable via sibling-directory prefix
- Where: `src/Cli/Program.cs:851` (core)
- Category: correctness · Effort: small
- `StartsWith(root)` without a trailing separator admits `/home/x/proj-backup` when root is `/home/x/proj`; localhost-only binding limits exposure but any local page can reach it.
- Fix: compare against root + separator; reject raw `..` segments.

### M11. Rig protocol relies on fixed Thread.Sleep polling
- Where: `src/Cli/Program.cs:1662` (core)
- Category: standards · Effort: medium
- 50 ms consumption hops, unconditional 1.5 s pacing, 2 s capture polls, all synchronous, encoding plugin timing assumptions.
- Fix: FileSystemWatcher + async/await centralized in a small RigClient class.

### M12. No request timeout/cancel; a hung solve wedges the UI behind the modal
- Where: `viewer/index.html:602` (viewer)
- Category: ux · Effort: small
- The busy flag guards double-submits, but there is no AbortController, no timeout, and no cancel button on the overlay.
- Fix: AbortController per query plus a cancel button that clears busy.

### M13. WASD key state leaks across focus loss
- Where: `viewer/index.html:905` (viewer)
- Category: correctness · Effort: small
- Held keys latch when the window blurs mid-press; Space on a focused button also thrusts the camera; listeners collect keys even in 2D mode.
- Fix: clear on blur and stop(); exempt buttons; listen only while 3D is live.

### M14. Heatmap encodes reachable/unreachable by red-green hue alone
- Where: `viewer/index.html:503` (viewer)
- Category: accessibility · Effort: small
- Classic deuteranopia failure pair at similar lightness on the core diagnostic view.
- Fix: add a second channel (hatch/outline) or switch to a blue/orange pair.

### M15. Filter selects have no accessible names; muted text fails AA in light theme
- Where: `viewer/index.html:294` and `:12` (viewer)
- Category: accessibility · Effort: small
- `title` is not a reliable accessible name and chosen values lose their context; light-mode `--muted` computes ~4.4:1 on the smallest text.
- Fix: aria-labels or visible labels; darken light-mode muted to ≥4.5:1.

### M16. Plugin has no Unload override
- Where: `rig/CalibrationThrower/CalibrationThrowerPlugin.cs:76` (plugin)
- Category: correctness · Effort: small
- In-flight captures are dropped unflushed on reload and beams orphan permanently (new instance starts with empty lists).
- Fix: Unload flushes `_tracked` and removes all valid beams.

### M17. Non-atomic request-file protocol (plugin side)
- Where: `rig/CalibrationThrower/CalibrationThrowerPlugin.cs:149` (plugin)
- Category: robustness · Effort: medium
- ReadAllText then Delete is a two-step claim; a writer landing between them is silently deleted unread, and the CLI counts disappearance as consumed.
- Fix: claim by rename before reading; unique per-writer filenames the plugin globs.

### M18. Malformed request.json re-parsed 8 times per second forever
- Where: `rig/CalibrationThrower/CalibrationThrowerPlugin.cs:150` (plugin)
- Category: robustness · Effort: small
- Delete only runs after successful parse, so a permanently bad file spams the console and wedges the channel; the offending content is never logged.
- Fix: retry once, then quarantine to `request.json.bad` with the raw content logged.

### M19. Culture-sensitive float parsing in chat commands
- Where: `rig/CalibrationThrower/CalibrationThrowerPlugin.cs:511` (plugin)
- Category: correctness · Effort: small
- On comma-decimal locales `!test here 1 80.5` parses 80.5 as 805; coordinate chat output is also culture-sensitive.
- Fix: invariant-culture parse/format everywhere user numbers cross the boundary.

### M20. IPC requests and markers carry no map identity
- Where: `rig/CalibrationThrower/CalibrationThrowerPlugin.cs:380` (plugin)
- Category: correctness · Effort: medium
- markers.json is map-agnostic and the watcher hardcodes dust2 geometry, so `!test` on another map silently solves against the wrong world.
- Fix: include Server.MapName in payloads, key markers per map, watcher selects assets by map and refuses unknown ones.

### M21. `_lastLineup` is global across players and survives map changes
- Where: `rig/CalibrationThrower/CalibrationThrowerPlugin.cs:530` (plugin)
- Category: correctness · Effort: small
- Player B's `!lineup` overwrites player A's pending `!goto`; after a map change `!goto` teleports to old-map coordinates.
- Fix: key by player slot; clear on map start.

### M22. Marker data assumed well-formed
- Where: `rig/CalibrationThrower/CalibrationThrowerPlugin.cs:384` (plugin)
- Category: robustness · Effort: small
- Hand-edited markers.json or short beam/store arrays throw unhandled exceptions inside command handlers.
- Fix: validate shapes on load, drop bad entries with a named warning, length-check request arrays.

### M23. Per-tick LINQ and collection allocations in TrackProjectiles
- Where: `rig/CalibrationThrower/CalibrationThrowerPlugin.cs:309` (plugin)
- Category: performance · Effort: small
- Fresh HashSet + Where().ToList() + per-projectile arrays every tick on the latency-sensitive thread.
- Fix: reuse cleared class-level scratch collections.

### M24. 700+ KB of three.js loads synchronously even for 2D-only sessions
- Where: `viewer/index.html:338` (viewer)
- Category: performance · Effort: small
- Blocking mid-body script tags fetch and evaluate three.js on every load though 3D is opt-in.
- Fix: lazy-load inside ensure3d, or at minimum defer in head.

### M25. start() does not guard an already-live render loop
- Where: `viewer/index.html:942` (viewer)
- Category: performance · Effort: small
- The toggle race can stack concurrent rAF chains, doubling GPU work and double-updating damped controls.
- Fix: `if (live) return;` or track and cancel the rAF id.

### M26. O(n²) indexOf scans in draw, hit-testing, and sync3d
- Where: `viewer/index.html:511` (viewer)
- Category: performance · Effort: small
- `result.lineups.indexOf(l)` inside loops makes every hover/pan frame quadratic on map-wide result sets.
- Fix: stamp indices once when results arrive.

### M27. Floating cards/panel/legend collide on small windows; controls anchored to magic 44px
- Where: `viewer/index.html:84` (viewer)
- Category: ux · Effort: medium
- Toolbar wrap pushes content under the absolutely-positioned cards; below ~500px everything overlaps with no responsive collapse.
- Fix: anchor #controls inside .stage; small-viewport media query collapsing cards to details.

### M28. Status changes and the solve overlay are silent to assistive tech
- Where: `viewer/index.html:281` (viewer)
- Category: accessibility · Effort: small
- All feedback flows through textContent swaps with no aria-live; the modal has no dialog semantics.
- Fix: role=status aria-live on #status; role/aria-busy on the overlay.

### M29. Single 1,034-line viewer file with ~14 shared mutable globals
- Where: `viewer/index.html:369` (viewer)
- Category: standards · Effort: large
- The root enabler of the 3D lifecycle bugs; native ES modules work without a build tool.
- Fix: split into app.css + modules (state, map2d, view3d, api, panel).

### M30. No Cache-Control/ETag on any HTTP response
- Where: `src/Cli/Program.cs:859` (core/viewer)
- Category: performance · Effort: small
- Heuristic caching refetches the 607 KB three.js per session yet can serve stale viewer-map.json after a rebuild.
- Fix: no-cache+ETag on data/ and index; max-age on viewer/lib; ETag keyed on build for /api/mesh.

### M31. dotnet run per request instead of a published binary
- Where: `rig/watcher.sh:8` (tooling)
- Category: standards · Effort: small
- Every in-game request pays MSBuild evaluation, and a mid-edit broken tree turns into silent in-game failures.
- Fix: dotnet publish once to rig/bin; watcher runs the stable binary.

### M32. Unquoted $CLI relies on word splitting
- Where: `rig/watcher.sh:19` (tooling)
- Category: standards · Effort: small
- SC2086 on every use; the leading-space `--target " $TARGET"` hack is undocumented.
- Fix: bash array invocation; document or fix negative-number argument handling.

### M33. No error handling strategy in the watcher loop
- Where: `rig/watcher.sh:1` (tooling)
- Category: standards · Effort: small
- No set -u/-o pipefail; any JSON extraction failure silently yields empty variables passed to the CLI.
- Fix: set -u -o pipefail, per-request functions, single parse emitting all fields, skip-and-log on failure.

### M34. summarize-run.py regex-parses markdown while the CLI writes JSON
- Where: `rig/summarize-run.py:16` (tooling)
- Category: standards · Effort: small
- Four regexes tied to human-facing wording silently match nothing if wording changes; the sibling .json has every field.
- Fix: read the JSON report; exit non-zero when absent.

### M35. Wait-then-write logic duplicated across three relay scripts
- Where: `rig/relay-plineup.py:44` (tooling)
- Category: standards · Effort: small
- Three drifting copies of the IPC send; fixes must be applied thrice.
- Fix: one shared calibipc.py module with atomic rename and timeout-as-error.

### M36. Absolute repo path hardcoded in five files
- Where: `rig/relay-chat.py:8` (tooling)
- Category: standards · Effort: small
- Moving the repo or changing user breaks the rig silently.
- Fix: derive from script location or one SMOKESOLVER_CALIB_DIR env var.

### M37. Plugin DLL deployed by hand with visible file drift
- Where: `rig/CalibrationThrower/CalibrationThrower.csproj:1` (tooling)
- Category: ops · Effort: small
- Deployed dll and runtimeconfig timestamps already disagree; no record of the deployed revision.
- Fix: deploy script (publish + rsync + css_plugins reload via the request channel).

### M38. No logging anywhere in the rig tooling
- Where: `rig/watcher.sh:18` (tooling)
- Category: ops · Effort: small
- Watcher echoes to a doomed stdout; python helpers log nothing, even on swallowed exceptions.
- Fix: journald via systemd for the watcher; shared python logging to a rig log.

### M39. Top-level CLI has no exception handling
- Where: `src/Cli/Program.cs:45` (core)
- Category: standards · Effort: small
- Expected user errors (bad path, malformed float) print raw stack traces.
- Fix: catch expected exception types at dispatch; message + usage + exit code.

### M40. Static files served via full in-memory ReadAllBytes
- Where: `src/Cli/Program.cs:867` (core)
- Category: performance · Effort: small
- A multi-hundred-MB GLB request spikes memory by the full file and stalls the single-threaded loop.
- Fix: stream with CopyTo and ContentLength64.

### M41. SkiaSharp consumed via transitive dependency
- Where: `src/Cli/Program.cs:528` (core)
- Category: standards · Effort: small
- Cli uses SKBitmap with no direct PackageReference; a VRF update can break the build inexplicably.
- Fix: explicit pinned PackageReference plus Linux native assets.

### M42. GLTFLoader vendored and loaded but dead
- Where: `viewer/index.html:340` (viewer)
- Category: standards · Effort: small
- 103 KB parsed on every load; THREE.GLTFLoader is never referenced since the GLB path was disabled.
- Fix: delete the script tag and the vendored file.

### M43. LineupSolver has zero test coverage
- Where: `src/Solver/LineupSolver.cs:38` (core)
- Category: testing · Effort: medium
- Range pruning, stability gating, nav sampling, and PointInPolygon are all untested pure functions.
- Fix: synthetic-mesh tests including empty-zone and concave-polygon cases.

### M44. Duplicated magic numbers across the viewer
- Where: `viewer/index.html:483` (viewer)
- Category: standards · Effort: small
- Bloom radius 144 (2D and 3D), pick radius 12/scale twice, heat cell 24 mirroring the server, eye height 64 in the parser.
- Fix: hoist named constants and one shared nearestLineup helper.

### M45. Theme and devicePixelRatio changes do not propagate to the 3D view
- Where: `viewer/index.html:1025` (viewer)
- Category: correctness · Effort: medium
- OS theme flips leave 3D in the old palette; DPR changes across monitors leave both canvases blurry.
- Fix: recolor scene in the theme handler; re-check devicePixelRatio in resize paths.

## Low

### L1. TriangleRaycaster.Blocked is a linear scan over all region triangles
- Where: `src/Sim/TriangleRaycaster.cs:42` (core)
- Fix: reuse TriangleCollider's grid with the vision filter and delete this class.

### L2. Null-forgiving Deserialize on user-supplied files
- Where: `src/Cli/Program.cs:400` and five siblings (core)
- Fix: a LoadJson<T> helper reporting "file X is not a valid Y".

### L3. ExportGltf leaks Package and Resource IDisposables
- Where: `src/Cli/Program.cs:1924` (core)
- Fix: `using` both, matching MapExtractor's pattern.

### L4. MeshPayloadCache is a mutable static ignoring its filter argument
- Where: `src/Cli/Program.cs:1949` (core)
- Fix: build once at serve startup and pass the bytes in; delete the static.

### L5. Physics magic numbers duplicated instead of named constants
- Where: `src/Sim/GrenadeTrajectory.cs:119` (core)
- Fix: BaseGravity, FloorNormalZ, ContactBackoff, NoNormalFilter constants.

### L6. Usage text omits bestlineup, pointlineup, exportgltf
- Where: `src/Cli/Program.cs:30` (core)
- Fix: generate usage from the command dictionary.

### L7. Quadratic model-entry lookup in solid entity extraction
- Where: `src/Extraction/MapExtractor.cs:144` (core)
- Fix: one dictionary keyed by lowercase path before the entity loop.

### L8. CollisionMesh round-trip test misses AttributeInteractAs and V1 fallback
- Where: `tests/Sim.Tests/VoxelGridTests.cs:46` (core)
- Fix: extend asserts; add GrenadeSolidFilter and out-of-grid Occlusion tests.

### L9. Abandoned rcon.py dead code
- Where: `rig/rcon.py:1` (tooling)
- Fix: delete it; drop rcon_password from server.cfg once unused.

### L10. Re-marking a name orphans the old beam; round restarts desync beam state
- Where: `rig/CalibrationThrower/CalibrationThrowerPlugin.cs:427` (plugin)
- Fix: RedrawMarkerBeams from css_mark and after restarts; prune invalid handles.

### L11. _showTestSmokes resets on reload with no persistence
- Where: `rig/CalibrationThrower/CalibrationThrowerPlugin.cs:533` (plugin)
- Fix: persist in a settings JSON or FakeConVar; accept a `show` field on throws requests.

### L12. Reply helper duplicated across five command handlers
- Where: `rig/CalibrationThrower/CalibrationThrowerPlugin.cs:437` (plugin)
- Fix: one shared Reply extension.

### L13. Chat color codes as raw unicode escapes
- Where: `rig/CalibrationThrower/CalibrationThrowerPlugin.cs:212` (plugin)
- Fix: ChatColors constants plus one [calib]-prefix helper.

### L14. Magic strings/numbers for designer names, item def 45, team 2
- Where: `rig/CalibrationThrower/CalibrationThrowerPlugin.cs:230` (plugin)
- Fix: named constants; `(int)CsTeam.CounterTerrorist`.

### L15. Relay scripts hardcode the calib path the plugin gets from an env var
- Where: `rig/relay-chat.py:8` (plugin/tooling)
- Fix: read SMOKESOLVER_CALIB_DIR with a repo-relative fallback.

### L16. Dead .toolbar button/select CSS and false click affordance on lineup cards
- Where: `viewer/index.html:62` (viewer)
- Fix: delete dead rules; drop cursor:pointer or wire the card click.

### L17. Tooltip flip logic can go negative near left/top edges
- Where: `viewer/index.html:790` (viewer)
- Fix: clamp after flipping.

### L18. Redundant rAF scheduling can run several full redraws per frame
- Where: `viewer/index.html:760` (viewer)
- Fix: a drawQueued guard around one scheduleDraw helper.

### L19. fly() allocates three Vector3 per animation frame while keys held
- Where: `viewer/index.html:913` (viewer)
- Fix: reusable module-scope temporaries.

### L20. API strings injected via innerHTML
- Where: `viewer/index.html:583` (viewer)
- Fix: createElement/textContent or a tiny esc() helper.

### L21. Inconsistent control styling across cards, paste row, copy buttons
- Where: `viewer/index.html:187` (viewer)
- Fix: one .btn base class with a small variant.

### L22. Theme-only 3D palette / stale DPR (see M45 for the full item)
- Where: `viewer/index.html:1025` (viewer)
- Cross-referenced; tracked under M45.

### L23. Heatmap legend text lives only in the status bar
- Where: `viewer/index.html:503` (viewer)
- Fix: fold coverage colors into the map key alongside the marker legend.

### L24. Stray scratch files in the live IPC directory
- Where: `data/calib/request_test.json` (tooling)
- Fix: delete; document the directory's file contract at the top of watcher.sh.

---

## Appendix: region assessments

**Core**: the physics stack is strong, measured, and well-commented, but it is wrapped in a 1,983-line monolith whose serve/validate/cache boundaries carry the highest-risk defects.
Priorities: harden the I/O boundaries, decompose Program.cs, and put tests around the exact-physics code that decides every answer.

**Plugin**: purpose-built and knowledgeable, but its three load-bearing mechanisms (signature scan, meta queue, file IPC) all fail silently, and tick-thread hygiene (74 KB synchronous appends, untracked timers, no Unload) makes reload-heavy sessions risky.
A startup self-test, spawn-matching meta correlation, and rename-based claims remove most of the risk.

**Viewer**: good bones (correct HiDPI 2D, guarded busy flag, unified focus styles) with the debt concentrated in the ad-hoc 3D lifecycle and in accessibility, which currently fails the keyboard and contrast bars the rest of the UI is close to meeting.

**Tooling**: a well-conceived closed loop undermined by prototype-grade operations: no supervision, message-dropping IPC, and error handling that converts every failure into a plausible "no result".
Systemd units, atomic renames, one shared IPC module, and a published binary convert it from a rig that rots between sessions into one that survives them.

## Audit 2026-08-30

An eight-pass multi-agent review (code quality, security, performance, test coverage, architecture, API, frontend, devops) found 58 issues.
30 were fixed the same day, verified by a clean build, 147/147 tests (55 of them new), a successful Docker image build, and syntax checks on the touched JS and Python.
Notable fixes with behavior changes: the solve cache path now respects --root; the crouch-only filter now covers pinned and exact-click origins (QueryVersion bumped 15 -> 16); on low-memory devices a missing mobile GLB no longer falls back to the OOM-sized desktop GLB (the device stays on the flat mesh with a console error); the viewer version token moved to ?v=17.
The 28 findings below remain open; most need a design decision or coordination with deploy.

### Open - critical

- **A30-C1 (devops)** docker-compose.yml / deploy flow.
  Deploy is manual with no verification that the gitignored data files (entities.json, mobile GLBs, prosmokes.json, standspots.json) actually landed on the prod host; the documented failure mode is prod silently serving degraded data.
  Fix: a rig/deploy-app.sh that rsyncs the required data globs, pulls a pinned image tag, runs compose up, and asserts each expected file is present and non-empty plus a health probe before declaring success.
- **A30-C2 (test-coverage)** tests/Sim.Tests/GoldenReplayTests.cs:36.
  The golden replay test silently returns (green) when the gitignored fixtures (de_dust2.s2geo, calib captures) are missing, which is every CI run, so CI cannot catch a physics regression.
  Fix: commit a small trimmed fixture geo plus a handful of capture lines, or make the skip loud in CI.

### Open - high

- **A30-H1 (api)** src/Cli/Commands/ServeCommand.cs (solve task).
  RunTargetQuery/SolveForTarget take no CancellationToken, so an abandoned request runs its full multi-minute solve and pins one of only two SolveGate slots.
  Fix: thread context.RequestAborted into the solver's Parallel loops; decide whether a cancelled solve should still write the cache.
- **A30-H2 (devops)** docker-compose.yml:11.
  The compose file defaults to the mutable :latest tag and there is no scripted pull/up/rollback, so the running git sha is unknowable.
  Fix: pin to the sha tag CI already publishes, bumped by the deploy script from A30-C1.
- **A30-H3 (devops/security)** Dockerfile runtime stage.
  No USER directive, so the network-exposed process runs as root with root write access to the bind-mounted ./data.
  Fix: USER app (the aspnet image ships uid 1654), after confirming the host-side data directory stays writable for that uid at deploy.
- **A30-H4 (frontend)** viewer/index.html #map canvas.
  The 2D radar has no tabindex and no keydown handling, so a keyboard-only user can never place a target or select a marker.
  Fix: tabindex plus an arrow-key cursor with Enter/Space activation, designed to match the existing focus styles.
- **A30-H5 (performance)** src/Cli/Services/TargetSolver.cs:132.
  Every uncached /api/lineup solve rebuilds the grenade and player TriangleColliders per request even though MapEntry caches both for the process lifetime; the sibling endpoints reuse them.
  Deferred because reusing the full-mesh collider changes the collision region the sim sees; needs a bit-identical (or accepted-diff) verification pass before switching.
- **A30-H6 (test-coverage)** src/Cli/Services/TargetSolver.cs:38.
  SolveForTarget, the single function wiring together the roof fallback, elevated origins, crouch filter, exposure flag, and pinning, has no integration test; the PR #12 nav-sliver regression is only pinned at the unit level.
  Fix: a synthetic-mesh TargetSolverTests that clicks into a nav sliver and asserts lineups are produced near floor height.

### Open - medium

- **A30-M1 (api)** ServeCommand /api/trajectory, /api/lineup-one, /api/slack.
  Required non-nullable numeric query parameters short-circuit in minimal-API binding with an empty-bodied 400, breaking the {error} contract for malformed requests.
  Fix: nullable parameters with manual validation, or problem-details middleware.
- **A30-M2 (api)** src/Cli/Services/LineupApi.cs QueryVersion.
  Cache invalidation on solver changes rests on remembering to bump the constant (a known past gotcha).
  Fix: derive the version component from the solver assembly's build id, or add a CI check that flags solver-directory diffs without a bump.
- **A30-M3 (architecture)** src/Cli/Services/LineupApi.cs.
  579 lines mixing four binary wire-format serializers, the query validator, cache-key derivation, and the solve orchestrator.
  Fix: split into a payload serializer and a query/orchestration service.
- **A30-M4 (architecture)** rig/CalibrationThrower/CalibrationThrowerPlugin.cs.
  A single 1,036-line class mixing entity/tick/IPC plumbing with 14 chat-command handlers.
  Fix: extract the command handlers into a router class.
- **A30-M5 (architecture)** src/Sim/GrenadeTrajectory.cs eye heights + viewer/js/state.js.
  The stand/crouch eye heights (64.06/46.04) are duplicated between the sim and the viewer with nothing enforcing sync.
  Fix: serve the constants from the API (e.g. alongside /api/maps) and read them at runtime.
- **A30-M6 (architecture)** viewer/js/validation.js.
  714 lines with zero imports, duplicating state.js helpers, and reading PascalCase report JSON while every other module reads camelCase lineups.
  Fix: wire it into the shared module graph and standardize the report casing.
- **A30-M7 (devops)** Dockerfile.
  No HEALTHCHECK (note: the aspnet image has no curl/wget, so it needs a dotnet-based or compose-level probe) and base images float on the 10.0 tag.
  Fix: add a liveness probe tolerant of CPU-pegged solves, and pin base images to a digest bumped deliberately.
- **A30-M8 (devops)** .github/workflows/ci.yml.
  No lint/format gate.
  Fix: run dotnet format --verify-no-changes locally first, fix the drift it finds, then add it as a CI step.
- **A30-M9 (performance)** src/Sim/TriangleRaycaster.cs:42.
  Blocked() is a linear scan over every region triangle, called once per verified lineup for the exposure flag.
  Fix: reuse a CSR-bucketed collider path so each ray is O(cells crossed).
- **A30-M10 (test-coverage)** src/Cli/Commands/ViewerDataCommand.cs radar z-band.
  The PR #12 fix (nav-band-bounded radar collider) has no test pinning a high-z map.
  Fix: extract the navZ-grid/radar-bounds computation and test it with a synthetic map at z=10000.
- **A30-M11 (test-coverage)** src/Extraction/MapExtractor.cs.
  The static-prop collision extraction has no tests below full re-extraction.
  Fix: split out the pure-geometry helpers (TriangulateHull, transforms) and unit test those.

### Open - low

- **A30-L1 (api)** ServeCommand mid-stream NDJSON errors are indistinguishable from empty results in the viewer; consider a {"fatal":true} field.
- **A30-L2 (architecture)** the lineup JSON contract is consumed by bare string access across 6 viewer files; a single JS constants module for field names and ThrowType strings would make renames fail loudly.
- **A30-L3 (devops)** docker-compose.yml requires a pre-existing traefik_proxy network with no documentation or pre-flight check; belongs in the A30-C1 deploy script.
- **A30-L4 (frontend)** viewer/js/main.js spawn/pro-smoke fetch failures are indistinguishable from maps with no data; surface a small non-blocking note on genuine failure.
- **A30-L5 (performance)** src/Cli/Services/TargetSolver.cs nav-fallback samples the square bounding box and discards the ~21% outside the reach circle only after paying for elevated/pinned augmentation; filter earlier.
- **A30-L6 (test-coverage)** src/Solver/StandSpots.cs wander-budget reset on nav-covered ground has no direct test; needs a crate-chain-with-walkway synthetic case.

## Adversarial review 2026-08-30

Six domain experts (physics, computational geometry, Source engine internals, validation methodology, solver algorithms, asset pipeline) audited the stack with a mandate to prove it wrong.
Their recommendations were then verified before implementation, and three were falsified by measurement rather than shipped on authority.

### Falsified by experiment - do not revisit without new evidence

- **"MollerTrumbore loses precision at map-scale coordinates."**
  Claimed catastrophic cancellation in `edge1 = b - a` at de_vertigo's z~15,368, with localization as the fix.
  Measured over 20,000 randomized small triangles at real map extremes: localizing produces **bit-identical** results (max delta 0.0), and the existing float32 path is within 0.00008u of a float64 reference.
  Subtraction of nearby floats is exact (Sterbenz), so the inherited vertex quantization is in the data, not the arithmetic. No fix needed.
- **"Never-settling trajectories burn the full tick budget; an early-out is a double-digit-percent win."**
  Instrumented a real de_dust2 map-wide solve: 62.4M coarse simulations, 20.55 billion ticks, of which expired trajectories are **1.8% of calls and 3.4% of tick work**.
  Not worth an accuracy risk in the sweep's acceptance behaviour. Instrumentation was removed after measuring.
- **"Broad-phase and narrow-phase disagree on cell boundaries, so grid-flush triangles tunnel."**
  True in the letter (floor-based bucketing never asks the cell below a boundary-flush triangle) and harmless in practice: the cell that *contains* the triangle is always tested, and the skipped contact is exactly the shared face, which has no volume.
  Fixing it would expand the voxelization loop for every triangle. Pinned with a test (`ATriangleFlushWithACellBoundaryOverlapsBothNeighbours`) instead, so the asymmetry is documented rather than accidental.
- **`logic_collision_pair` entities** (suspected as a runtime-gated collision mechanism we cannot see statically): **zero instances across all 16 extracted maps**. Cleared.

### Fixed 2026-08-30

- **A30-C2 (golden replay silently green in CI) - CLOSED.**
  Added a `crop` CLI command that writes a region subset of a `.s2geo`, used it to commit a 950KB de_dust2 fixture covering exactly the region these throws fly through (the same region the test's collider already queried, so the replay is identical to one against the full map) plus the two capture records.
  The test now asserts the fixtures exist instead of returning early.
  Verified by experiment: a 10% `GravityScale` change makes it fail, and reverting makes it pass.
- **Validation scored undetonated throws.**
  A grenade that never detonated has no landing to grade; the cull heuristic only excluded fast-moving (engine-deleted) captures, so wedged ones were scored and a handful dominated a map's mean/p90/max (22 rows up to 2,019u).
  Both cases are now counted and excluded, and the report distinguishes `culled` (in flight) from `stuck` (wedged).
- **Break-state world toggles (glass broken / doors open).**
  Extraction routes doors (`func_door`, `func_door_rotating`, `prop_door_rotating`) to `EntityDoor` and breakables (`func_breakable`, `prop_dynamic` with health) to `EntityBreakable`; `/api/lineup` accepts `broken: ["glass","doors"]`, the GET endpoints accept `broken=<csv>`, each state gets its own collider and cache key, and the viewer exposes a World state control.
  Both solver stages honour it: the voxel grid through the attribute filter and the exact collider through `BuildGrenadeColliderExcluding` (the exact collider is built from the interactAs-based grenade filter and would otherwise have kept bouncing throws off removed glass).
- **`prop_dynamic` collision was never extracted.**
  Structural props the game treats as solid (de_nuke's vent slats, shutters) had no collision at all, so the solver offered throws from inside them.
  Now extracted with shared-VPK model lookup and per-instance scale.
- **`--attrs` compatibility guard.**
  Splitting doors and breakables out of `EntitySolid` would have made them non-solid for every deployment that names `EntitySolid` literally (compose, systemd, validate, bestlineup).
  Requesting `EntitySolid` now implies the groups that were split out of it.
- **Collision-mesh 3D view had no mobile tier.**
  The same decoded-memory budget the mobile GLB tier protects was being blown by the debug mesh (de_inferno: 2.7M triangles) plus a synchronous `computeVertexNormals` over it.
  Low-memory devices now skip normals and render unlit, matching the textured scene's deliberate trade.
- **Test coverage:** break-state machinery (9 tests), `TriBoxOverlap` agreement at real map coordinates (+-12,000 XY, z to 15,500) and at boundary-flush geometry.

### Open, ranked (from the same review)

1. **A30-C1 deploy verification** remains the top ops item, now with more to rsync (per-map meshes, stand spots, meshdiff overlays).
2. **Offline per-origin sweep table** - precompute each stand spot's angle-to-landing manifold per map (the `standspots` precompute pattern; ~2.7 min and ~17GB raw per map before compression) to collapse the 92%-of-solve coarse sweep into a lookup. The structural speed play; prototype on one map and check recall against the accuracy harness before committing.
3. **Validation sampling does not match usage** - median distance from a real pro landing to the nearest tested target is 343u on the best-covered map, and 17% of pro landings are >800u from any tested target. Stratify targets by pro-demo density, add confidence intervals to the dashboard (a "100% within 8u" at n=150 is really "0-2.5% miss rate"), and lock a fixed-seed nightly regression suite; the date-seeded fuzz targets make runs non-comparable.
4. **False-negative rate is unmeasured** - every validated lineup is one the solver already believed in. Replay pro-demo throw/landing pairs through the solver and count how many real, humanly-thrown smokes it would never have proposed. Needs no new in-game throws.
5. **Breakable classification needs ground truth** - the health-flag heuristic finds only some panes (de_nuke's roof glass and vent slats carry no health in the entity lump). Use the rig: shoot the pane, re-throw, compare.
6. **Stability is presented as reliability but barely predicts it** (r = -0.117 against real error, quantized to 5 samples). Either improve the estimator or stop framing it as a success probability.
7. **`RunSpeed` is a point sample**, not an engine constant: the engine adds the player's actual velocity (capped by `sv_maxvelocity`), so run-jump lineups assume the thrower hits the calibration rig's reference speed. Label it, and consider modelling it as speed-dependent.
8. **Plugin signature fragility** - replace the raw byte-pattern scan with CounterStrikeSharp's `VirtualFunction` (vtable + symbol) API and watch `ianlucas/cs2-signatures` as an early warning for CS2 updates.
9. **Pipeline items** - KTX2/BasisU textures (attacks decoded GPU memory directly), per-level radars for stacked maps (nuke/vertigo), replacing the bespoke SM3D wire format with glTF + meshopt, and server-verifying the top-N lineups actually shown to a user through the rig.

## Viewer session 2026-08-30 (evening)

- **Collision overlay washed out to white over the textured world.**
  The overlay used a lit material while the lights live in the flat scene and the textured scene has none, so the same mesh rendered magenta in one view and near-white in the other.
  Now unlit, and therefore identical in both. Verified by pixel diff: overlay pixels went from 12,549 near-white / 2,007 magenta to 73 / 24,563.
- **Spawn markers** were floating diamonds drawn with depth testing off, so they hovered at knee height and showed through the entire map, and they vanished entirely in textured mode (the spawn group was the one overlay missing from the scene re-parent list).
  They are now rings laid on the floor at the spawn position, depth-tested so map geometry occludes them, and they follow the textured scene like every other overlay.
- **Aiming overlays could both draw at once** (two independent toggles, two crosshairs over each other).
  One tri-state control now cycles off / centre crosshair / numbered lineup ruler, with the old preference migrated.
- **A 2D click could not say which floor it meant.**
  On de_nuke a point over Bombsite A is also over B and the roof; the solver resolves such a column to the LOWEST walkable height, so a click meant for the site silently targeted the floor below it.
  New `/api/levels` returns the stacked walkable levels for a column (128u apart to count as separate, so ramps and crates stay one level), each labelled with the callout a player standing there would be in, and the viewer offers them as a chooser whenever a click is ambiguous.
  A single-level click is unaffected.

### Still open for verticality

The chooser resolves the ambiguity once a click has happened, but the 2D radar still renders stacked maps as one flattened image (the ground-height grid keeps the lowest floor where levels overlap).
Valve ships a separate lower radar for exactly de_nuke and de_vertigo.
A per-level radar pass plus a level selector would let the user work on one floor at a time rather than disambiguating click by click.

### Mesh-diff tuning and a new render-pipeline bug (2026-08-30, later)

- **The diff painted real ground orange.** Its render mesh reused the 3D viewer's junk-material filter, which drops `materials/dev/` - and de_nuke paints those placeholder/reflectivity materials on floors people walk on.
  With no render surface under them, the map's own ground was reported as phantom physics geometry.
  `materials/dev/` is no longer treated as junk for the diff (tools/effects/UI still are), and the scan band above nav ground came down from 320u to 192u with a 40u nav-proximity gate, so structures nobody plays on (the reactor shell, the scaffolding over B) stop generating findings.
  de_nuke: 11,605 cells to 7,634, phantom-physics cells 4,544 to 2,405.
- **Pro smokes** is a 2D-only overlay and no longer offers itself in the 3D view.
- **OPEN - textured GLB assigns a water material to non-water surfaces.**
  On de_nuke's B site, the reactor shell and surrounding walls render with the water caustic texture. This is texture assignment in the exported GLB, not the viewer's shader-tint fallback (that path produces a flat tint, not a caustic pattern), so the suspect is the export/post-process chain: VRF export, `rig/fix-prop-scale.mjs`, `rig/optimize-textured-glb.mjs`.
  This is the second incident of this class (cs_italy's corrupted preview was a `fix-prop-scale.mjs` bug), which is the case for the pipeline review's recommendation to stop passing GLBs between separate Node scripts.
  First diagnostic step: render the raw VRF export and the post-processed GLB side by side for the same region and see which one first shows water on the reactor; `gltf-transform` in `rig/node_modules` currently fails to open the processed GLB (`EXT_texture_webp` plus a version mismatch), so that needs pinning before the comparison.

### Callout search: first slice of the UX direction (2026-08-30)

`GET /api/callouts?map=<name>` returns the map's own `env_cs_place` names with a position each (volumes sharing a name are merged), and the viewer's actions card now opens with a "smoke where?" search box backed by a datalist.
Typing a callout and committing it places the target there, so the flow reads **name to target to search** instead of hunt-the-pixel to target to search.
Verified end to end on de_nuke: 29 callouts, "BombsiteB" placed the target, the map-wide solve returned 16 lineups, and the first card carries its distance-to-target (28.4u) from the tolerance work.

Next steps on this direction, in order: an opinionated top-5 with a "matches pro usage" badge from the pro-demo data the viewer already fetches; favourites that persist; then precomputing the callout pairs people actually search so the hot path is a cache read rather than a 30s solve.

### Open: water-looking surfaces on de_nuke B

Investigated and **not** reproduced from the tool's side. The exported GLBs are structurally clean: desktop and mobile agree (522 materials, 236 vs 229 textures, 229 vs 222 distinct base-colour references), texture sharing is legitimate reuse of the same material across instances rather than dedup corruption, only 29 of 522 materials lack a base texture, and the map's only `csgo_water_fancy` surface is a 241x126u plane down in the vents area (hammer x -106, y -933..-692, z -899..-773), nowhere near the reactor hall.
Flying the camera into Observation renders correctly (green walls, concrete, control desk).
To chase this further the exact spot is needed - a `getpos` from where it looks wrong - since the effect is evidently localized and the asset data does not explain it.

### Viewer round three (2026-08-30)

- **The "water texture" on de_nuke B, diagnosed and fixed.**
  A screen-centre material probe at the reported position identified the surface as `caustics_004_color_mks_*.vtex` from `materials/de_nuke/hr_nuke/caustics_001_decal.vmat`, shader `csgo_static_overlay.vfx`.
  Caustics are the rippling light water casts onto nearby surfaces - in game a projected additive effect, but the exporter bakes them as ordinary OPAQUE geometry, so B's back wall and the reactor tower came out sheeted in solid blue ripples.
  Caustics decals are now dropped like the other effect-only surfaces, and every remaining `csgo_static_overlay.vfx` decal (bombsite letters, scuffs, door windows) renders alpha-blended with a polygon offset so it sits on the surface behind it instead of replacing it.
  Verified at the reported spot: the probe now returns the real ceiling material.
- **Aiming overlay is a segmented control** (off / crosshair / lineup ruler) rather than a cycling button, so all three states are visible and the active one is obvious.
- **World state is now visible in the 3D view.**
  The mesh payload gained separate index groups for doors and breakables (format version 2), and the viewer draws them as their own meshes that the World state control hides.
  Choosing "doors open" removes the door geometry from the picture, so the view matches the world the solve ran in. Verified by pixel diff at a de_nuke door: 585k pixels change.

### Resolved: door pose at round start

Confirmed in game - de_nuke's door is CLOSED at round start, so the compiled entity pose the extractor bakes is correct and the textured render (which shows it open) is the one that disagrees.
No solver change needed: intact stays the default and "doors open" remains the mid-round option.
Worth remembering when reading the mesh diff, since a door will legitimately show as a physics-only surface there.

### Device-appropriate UI (2026-08-30)

- The 3D help listed WASD, right-drag and pinch gestures to everyone. It now shows only the controls the device in use actually has, driven from a class main.js sets: the browser's own pointer report to start, overridden by the first real touch or mouse move, because hybrid laptops report touch capability while being driven by a mouse and some automation browsers report no hover at all.
- "Re-target" wrapped to two lines, which doubled the row height and stretched the copy button beside it into a slab. The label stays on one line now.
- Every explore control carries a short hover tooltip in the app's own palette, shown immediately rather than after the native title delay, and suppressed on touch where a tooltip would latch open after a tap.

### Opinionated results and persistent favourites (2026-08-30)

The panel used to answer "which throw do I use" with every lineup the sweep verified, paginated fifty at a time.
It now shows the **best five** by default, with `best 5 of N` and a `show all` toggle back to the full list.

Ranking (`lineupScore` in state.js) scores what decides whether a player lands a throw in a real round rather than what looks good in data: distance to the target dominates, a wall or corner pin subtracts heavily (it removes position error outright, which is worth more than any amount of simulated stability), then aim reference quality, exposure, stability and one-tick scatter, with bounces and flight time breaking ties.
A lineup thrown from within 160u of a real pro throw origin (the existing HLTV demo data) earns a ranking bonus and a `pro` badge - the strongest "this is the normal way to do it" signal the tool has.

Favourites now persist: keyed by the throw itself (feet to the unit, type, strength, angles to a tenth of a degree) in localStorage per map, so a saved lineup survives re-solving, filtering and a reload, and is always pinned into the top five.

Verified end to end on de_nuke BombsiteB: 16 lineups, top pick `mid (L+R) crouch 5b 2.5s 28.4u pro wall 1.5 deg 100%`, show-all revealing all 16, and a favourite surviving a full reload and re-solve still marked and pinned.

One trap worth recording: the callout block was first placed after the boot code, so `loadCallouts` ran during boot and hit its own `let callouts` in the temporal dead zone - the map loaded with zero callouts and only an "Uncaught (in promise)" with no message. Declarations used by anything the boot path awaits have to sit above it.

### Spawn/target usability and the pro-lineup question (2026-08-30)

- Sidebar widened to 172px so "Re-target" fits without wrapping or ellipsis.
- **Spawn markers were unclickable in 2D.** They are drawn at a fixed on-screen size but hit-tested with the tight lineup-marker radius, so a click beside one missed and solved from the raw pixel instead - defeating the only reason to draw them. Grab room now matches what the eye sees (marker radius + 14px).
- **3D spawn rings kept a constant world size at distance.** A clamp added with the ring rework capped the screen-space scaling, so distant spawns shrank. Removed: a spawn now reads the same size from across the map as up close.
- **The throw spot can be copied.** A one-spot solve remembers the origin it used and offers it as a `setpos`, so a position found by clicking can be quoted back.
- **Pro landings are clickable targets.** A landing recorded from a demo carries the height it actually landed at, which is exactly what a 2D click cannot express on a stacked map (see below).

#### Answered: are pro lineups verified by the solver?

No, and the distinction matters. The pro overlay is raw HLTV demo data - throw origins and landing spots parsed from real matches, never solved or verified here. Only the new `pro` badge on our own results means something stronger: that badge marks a **solver-verified lineup whose throw spot is where pros throw from**.

The reported case (target `1278 -1924 -223`, nothing findable from T spawn) was not a solver failure. `/api/levels` at that XY returns three stacked floors: Tunnels (-636), Garage (-412) and Garage (-224). The pro smokes near that point land at **z=-414**, the lower Garage floor; the target given was the **-224** floor, roughly 190u above it. Asked for the floor pros actually smoke, the solver finds it immediately: from the pro origin (-710,-1329) to (1281,-1950,-414) it returns a jump-throw landing **0.73u** from the target.

So the failure mode was the stacked-level ambiguity again, arriving through a path the level chooser does not cover - a pasted `setpos` carries an explicit z, so nothing asks which floor was meant. Clicking a pro landing now sets the target at the demo's own height, which sidesteps it for exactly the case where a player is trying to reproduce a pro smoke.

### Explainable ranking and 3D throw types (2026-08-30)

- **3D markers now carry the throw type.** Every lineup was the same sphere, so a stand throw and a run-jump were indistinguishable in 3D while the 2D map had encoded movement in marker shape all along. Shape now carries movement (ball grounded, squashed ball crouched, cone airborne, wedge run-jump) and colour keeps carrying the mouse buttons, so a lineup reads the same in both views.
- **The ranking shows its work.** `scoreBreakdown` returns the total with the reasons that produced it, in the order they applied, and the panel renders both: a score chip on every row and an expandable breakdown inside the opened lineup, e.g. `base 100 / lands 28.4u off -43 / wall pin - walk into it +60 / 5 bounces -30 / 2.5s in the air -10 / pros throw from this spot +45 = 122`. Higher is better, so the numbers read the way a person would say them.
- **Pro usage is now a switch, not a fixed thumb on the scale.** "count pro usage in ranking" (on by default) decides whether the +45 applies. With thousands of demo points on a map, the bonus could otherwise crowd out spots that are simply better throws, and the toggle is ranking-only - it never changes which lineups the solver found, so nothing re-solves.
- The pager is hidden in top-picks mode, where it would only repeat the "best 5 of N" line it sits above.

### Results panel rebuild and a measured (small) sweep prune (2026-08-30)

- The legend summary was printing the result count (`legend 400/400`), which belongs to the results panel and not to a colour key. Removed.
- **The panel had three competing headers**: a floating collapse button over the preview image, a pager strip, and a separate best/all bar. They are now one row - collapse, count, sort, best/all switch, and a compact `1/8` pager that only appears when the full list needs paging.
- **Results can be sorted**: best score (the default, and the same ranking the top picks use), closest to target, most reliable, fewest bounces, fastest.
- **The target section was rebuilt**: name a spot, click the map, or paste a getpos, in that order (naming is fastest), with the two copy-back actions on their own labelled row instead of crowded beside the primary button. Everything carries a tooltip.
- **The stacked-floor chooser is now loud**: accent border, its own shadow, a short appear animation and a status line naming the floor count. It was easy to miss in the corner, which defeats the point of asking.

#### The ballistic prune, measured

A vertical-reach prune was added to the sweep: a projectile at launch speed v cannot pass above the parabola of safety `v^2/2g - g*d^2/2v^2`, and a bounce only removes energy, so a zone above that envelope is unreachable from an origin at any angle or bounce count. Launch speed is overstated (jump and run velocity added outright, plus a 128u margin) so it can only discard the impossible.

Measured on de_nuke with a high target (roof, from T spawn): **331s user CPU with the prune against 342s without, about 3%**, with identical output. That is a real saving but nowhere near the hoped-for cut, and it fits the earlier profiling: the sweep is memory-bandwidth-bound and its cost is dominated by simulations the existing distance prune already gates. Kept because it is cheap, physically justified and costs no accuracy, but **the structural win is the precomputed per-origin table, not more per-origin tests.**

#### Noted: candidate counts are not deterministic

The same query returned 1,050 candidates on one run and 1,049 on another with identical final output. Bucket winners are decided by `Better` under `Parallel.ForEach`, and its 1u dead band makes the survivor depend on arrival order. Harmless for the answer (the ranked result was identical), but it means candidate counts cannot be used as a regression signal, and a future determinism requirement would need an order-independent tiebreak.

### Panel fixes, an honest pro badge, and a prune that actually pays (2026-08-30)

- **The score breakdown could not be opened.** The whole detail card carried the deselect handler, so clicking its summary (or anywhere near the console command) closed the lineup instead. Only the summary row collapses a card now, and the breakdown is labelled "why score N?" rather than a bare number.
- **Callout search is parked** behind a `hidden` attribute rather than deleted: the flow it belongs to (name a spot, get five answers) needs precomputed results behind it to feel instant.
- **Exact-spot search is back as its own mode.** "Search radius" now offers "exact spot (deep search)", which pins the reach to the API minimum, turns on the fine angle lattice and drops the verify gate to its floor, so a throw from precisely the clicked position is found even when it is awkward - instead of a 300u radius answering a different question.

#### The pro badge was badging almost everything

It matched a lineup's feet against any pro throw origin, ignoring where that pro's smoke landed. On a map with 901 recorded throws, that meant a spot near anywhere a pro ever threw anything earned the badge regardless of the target.

Measured on de_nuke, 200 sampled stand spots against an arbitrary target: **158 of 200 (79%) were badged "pro" under the old rule, and 0 under the new one**, which requires a single demo pair to match at both ends - the throw started near this lineup's feet AND that smoke landed near where this lineup lands (160u and 220u).

#### Free-space reachability: the prune that was worth building

A grenade has to travel through open air, so the shortest free-space path from a throw spot to the landing zone is a hard lower bound on the distance that throw must cover - and unlike straight-line distance, it knows that lower tunnels sit under a bombsite rather than next to it. `FreeSpaceReach` floods the free voxels outward from the zone (six-connected, so a diagonal never squeezes between two solid cells) and every origin is checked against its own type-and-power range before a single simulation runs.

Measured on de_nuke, high roof target from T spawn: **331s user CPU before, 286s after - a 13.5% saving with bit-identical output** (same lineup, same 1050 candidates). The second reference target came out identical too, at 264s. That is four times the return of the ballistic gate measured earlier, and it is the mechanism the ballistic gate could never capture: being unable to get there from inside, rather than being unable to throw that far.

### Scoring audit, pro emphasis, and card cleanup (2026-08-30)

**The score was audited against a real 400-lineup solve rather than tuned by feel, and it found two faults.**

- `scatter` was **never applied**. The scoring read `l.restScatter`; the API field is `l.scatter`. One of the reliability signals validation showed matters most - the one-tick foot-shift chaos measure - contributed nothing. It fires on 50 of 400 lineups now.
- The weights left **78% of results scoring negative**, with distance and flight time dominating everything that decides reproducibility. Re-weighted with caps (base 140, distance -1.2/u capped at -90, bounces -4 capped at -32, flight -2/s capped at -18, stability -70): the same set now runs min -43, median 36, p90 84, max 178, with 21% negative. Caps matter as much as weights - without them a far, bouncy throw accumulated unbounded penalty and buried the pin and aim-reference bonuses that actually decide whether a player can repeat the throw.

- **Pro lineups are marked, not just scored.** A red ring around the marker in both 2D and 3D, since shape already carries movement and fill carries the mouse buttons.
- **Tooltips were doubled.** The CSS bubble added earlier showed alongside the browser's own and was clipped by the scrolling sidebar. Removed; the native `title` is the single source.
- **The selected lineup card was rebuilt**: keep and discard are icons in the corners, the score folds behind "why score N?", and Share / Go to are one compact row instead of four full-width buttons.
- **Unreachable origins are dropped before the sweep**, not inside it, so the progress count reports spots that can actually produce a throw rather than tens of thousands the grenade could never leave. Result verified identical (same lineup, same 1050 candidates, 285s user).

### Recovering a stylesheet I destroyed, plus sweep ordering (2026-08-30)

**The whole app rendered unstyled, and the accuracy page with it.** An edit script computed `s[start:end]` where `end` preceded `start`, producing an empty string, and `str.replace("", new)` in Python inserts the replacement **between every character**. `viewer/app.css` went from 48KB to 14MB: 117 unique lines repeated 244,000 times.

The original was recoverable precisely because of how that failure works - the source characters were interleaved, not deleted - so removing every occurrence of the inserted block reconstructed the file byte for byte. Restored, verified (48,555 bytes, braces balanced, both pages render styled again).

**Rule for this repo: never call `replace` with a computed `old` without asserting it is non-empty**, and prefer anchored edits over index arithmetic. `git checkout` was not an option here: the file carried a session's worth of uncommitted work.

Also in this round:

- **Progress dots are viewport-culled in 2D.** A map-wide sweep streams tens of thousands of evaluated spots and the canvas redraws every frame while it runs; off-screen cells are skipped now, which is the difference between a smooth progress view and a stuttering one.
- **Filters and advanced settings have reset controls.** Defaults are read from the markup (`defaultSelected` / `defaultChecked`) rather than a second hardcoded list that could drift.
- **Practice setup moved out of the results panel** into advanced, where a one-time console paste belongs.
- **The sweep now runs nearest-first through open air.** Origins are sorted by the free-space distance the reachability flood already computed, so the search grows outward from the target rather than in stand-spot storage order, and the spots most likely to produce a throw resolve first. Ordering only changes when each origin is reported - every reachable origin is still swept - and the reference solve is bit-identical (same lineup, same 1050 candidates).

### The sweep really does flood outward now, and the floor question blocks (2026-08-30)

**The "expanding search" was still painting diagonal stripes** even after origins were sorted nearest-first. Cause: `Parallel.ForEach` range-partitions a list, handing each worker a static contiguous slice, so eight workers ran eight different radii simultaneously. Measured on a de_nuke solve, the first 4,000 reported origins ranged from 82u to 2,814u from the target at any moment.

Switching to `Partitioner.Create(origins, EnumerablePartitionerOptions.NoBuffering)` makes workers pull the next origin one at a time, so the front stays together. Re-measured: mean distance now climbs 153u, 303u, 391u, 526u, 602u, 685u across successive batches with tight bands, which is an actual flood outward from the target. Each item is a whole set of simulations, so per-item hand-out costs nothing next to the work it hands out.

Also this round:

- **The stacked-floor question is now a modal** over a blurred app, and `Search map` is disabled until it is answered. Solving the wrong floor costs a full sweep and answers a question nobody asked, so it should not be dismissable by ignoring it; a cancel link clears the target for anyone who wants out.
- **Filters/advanced reset controls are icons**, and reset now restores each select's `data-default` - the same value the "(N) active filters" counter compares against. They disagreed before, so a reset left the card reporting "(1)" forever.
- **The sidebar scrollbar is thin and palette-matched** instead of the browser's default slab against the cards.
- **The selected lineup card**: favourite moved to the left, a clipboard button beside the remove ×, and the monospace console-command row deleted (the clipboard carries it). The score breakdown now hangs off the score chip itself - click the number whose meaning is in question - instead of a separate "why score N?" disclosure.

### Selection paging, collapse, and an honest pro badge (2026-08-30)

- **Selecting a lineup jumped to a random page.** The page was computed by finding the selection in the *unsorted* result set while the list rendered the *sorted* one, so clicking the top result scrolled to wherever that lineup sat before sorting - page 7 of 8. Both now use the ordered list.
- **A collapsed results panel could not be reopened.** The collapse control had moved inside the panel header, and the collapse rule hid every child except the control itself - which was now a grandchild - leaving a dot with nothing to click. The header survives collapse with only that control visible (verified: 22x22 and clickable while collapsed, full header back on expand).
- **Advanced reset is a header icon**, matching filters, instead of a full-width button at the bottom of the card.

#### The pro badge on a truck roof: a data limitation, now named

`rig/parse-demo-smokes.py` recorded throw origins as `[x, y, side]` - **no height**. A stand spot on top of a truck therefore matched a pro who threw from the ground beside it, which is exactly the nonsense badge reported.

The parser now keeps the thrower's height (`[x, y, z, side]`), and the viewer requires it to agree within 48u when present. Existing per-map data predates that, so the matcher treats a three-element origin as area-level: the radius tightened to 120u, the badge reads "pros smoke this from around here", and the bonus drops from +45 to +25. Re-parsing a map's demos upgrades it to the strict test automatically, with no viewer change.

**The rings are gone from both map views.** With hundreds of matching markers wherever a popular throwing area was on screen, per-marker circles buried the map. The results list carries the signal.

#### Audit: are the clustered garage lineups real?

Solved a garage target (400 lineups), sampled 8 at random, and re-simulated each one through a completely separate path - the CLI's `throw` command driving the exact integrator from the reported feet, angles, type and click.

**All 8 reproduced within 0.05u of the rest point the solver reported**, landing 1.7u to 26.6u from the target, all inside the 80u tolerance asked for. The clustering is real: a garage target genuinely is reachable from a wide spread of spots, and the solver is not inventing them.

#### Flood shape

Measured on an open-area dust2 target: the first 100 origins average 89u from the click (min 36u, max 160u) and spread across all four quadrants (18/41/27/14). It does start at the click and grow outward. It is not a circle, and should not be - the order is distance **through open air**, so it follows doorways and stops at walls rather than expanding through them.

### Round: simpler results, real diff patches, clickable spawns (2026-08-30)

#### The "best 5" mode is gone

The list sorts by the same ranking the mode used, so the best five were already the top of page one.
Keeping a switch that showed a subset of what the page below it showed cost a control, a header state, and a paging special case, and taught the reader nothing.
`topPicks`, `state.topOnly` and the `show all / show best` toggle are removed.

#### Mesh diff renders as patches, not billboards

The 3D overlay drew each mismatched cell as a `PointsMaterial` sprite, which turns to face the camera: a mismatch on a floor and one on a wall looked identical, and neither sat on the geometry it was reporting.
It is now one `InstancedMesh` of flat 16u quads (the diff's own cell size) laid at the cell positions, with polygon offset so a patch sits in front of the surface it covers.
The diff scans straight down each column, so every cell it records is a surface a vertical ray crossed - a horizontal quad is the honest shape for it.

**Trap hit on the way:** the material was carried over with `vertexColors: true`, and three.js reads per-vertex colour and per-instance colour through the same varying.
With no colour attribute on the geometry the varying stayed at zero and **every patch rendered solid black** - visible only once depth testing was switched off to prove the patches were drawing at all.
Per-instance colour needs `instanceColor` alone; `vertexColors` must be off.

#### Mesh diff ignores out-of-bounds space

Being near a walkable polygon is not the same as being in play.
The column filter admits a vertical band around each nav point, so it also admitted the inside of the wall beside it and the void under a catwalk - places the two meshes disagree constantly and where no smoke will ever land.

`MeshDiffCommand.KeepReachable` now floods open air outward from the walkable surface (16u voxels, six-connected, seeded a voxel above each nav ground point) and keeps only cells with reachable air on one of their six sides.
On de_nuke that drops **1786 of 7634 cells (23%)**, added none, and the drops cluster in the outdoor dead space behind the building (the densest dropped 256u buckets keep 0-6 cells each), which is exactly the region meant.
No hand-tuned distance is involved: unreachable is out of bounds by construction.

#### Spawn markers are clickable again

The 2D handler tested `if (state.picking || !state.target) { setTargetAt(...); return; }` **before** any spawn hit test, so every click without a target was swallowed as "set the target here".
Clicking a spawn first - the obvious way to ask "what can I throw from spawn?" - put the target on the spawn and the marker appeared inert. The 3D ring had the same hole, and silently did nothing at all.

Both now test the spawn first. With a target set it solves from that spawn as before; without one the spawn is **held** (`state.pendingOrigin`) with the status line "throwing from T spawn - now click the spot you want to smoke", and spent on the solve the moment a target arrives - including after the stacked-floor chooser answers.

Verified end to end in a real browser: spawn click with no target holds the origin, the next click sets a target, the floor chooser resolves it, and the solve runs from the spawn's exact coordinates.

#### The result row speaks like a lineup guide

Research pass over how the established resources present one lineup (csnades, csdb.gg, cs2util, NADR/Refrag, Valve's in-game Map Guides, SMOKEPRACTICE) found none of them shows a raw offset, bounce count, or reliability percentage; difficulty, where shown at all, is one word; and movement is always spelled out in full English.
Our row carried up to thirteen signals in solver units.

Simple mode (default) is now four facts in the order every tutorial teaches them: **which button · how you are standing**, a difficulty word, and **what you aim at** in plain language, plus a warning line only when there is one ("Seen while throwing", "Pro pick").
The word comes from `difficultyWords`: pinned or steady-and-edged is Easy, steady with something to aim at is Reliable, unsteady or a sky shot is Tricky, everything else Needs practice.
Measured over a real 400-lineup mirage solve it splits 102 / 84 / 87 / 127, so it discriminates rather than labelling everything the same.

The telemetry is not lost. A `detailed` toggle in the results header (remembered per browser) puts bounces, flight time, exact miss distance, stability percent and aim degrees back on the row, and the opened card's **Show details** disclosure carries the full score working ending in "Match score" either way.

#### The ordered flood costs nothing

Measured on the same cold mirage map-wide solve, cache dropped before every run:
ordered flood (free-space distance sort + `NoBuffering` partitioner) **50.3 / 46.4 / 46.2 / 44.6s**; plain range partitioning **62.0 / 49.2 / 49.1s**.
Same 400 lineups either way. The ordering is one sort over ~24k origins and does not slow the sweep down - if anything it is slightly faster.

### Round: three control rows, honest pro badges, real diff surfaces (2026-08-30)

#### Two of the "still broken" reports were right, and one was a different bug

**3D spawn clicks never worked.** The marker was a `RingGeometry`, and the ring's hole was also a hole in the click target: a raycast aimed at the middle of a spawn passed straight through to the floor behind and set a target there.
Verified by dispatching a real pointer sequence at the marker's projected screen position - it returned a target 300u away rather than the spawn.
The marker is now a filled disc plus a rim, and both carry the spawn tag so a click anywhere on it means the same thing.

**2D spawn clicks worked but looked like they had not.** With no target set, the click is held (`state.pendingOrigin`) and the only feedback was a line of status text at the top of the screen.
The held spawn is now drawn in its own colour with a ring around it, in both views.

**The panel did not collapse.** `#lineup-list` carries its own `display: grid`, and an ID selector beats the four-class structural rule that was meant to hide it, so a "collapsed" panel stayed 257px wide - still covering the map it was collapsed to reveal.
ID-qualified now: 340px open, **34px collapsed**.

#### One control row per question

The 3D button, two icon strips and a segmented control were scattered down the card with nothing saying which of them were exclusive.
Now three rows that all look the same, with the row label carrying the meaning: **View** (2D / 3D / Textured, one at a time), **Overlays** (Spawns / Collision / Top-down / Mesh diff, any combination), **Aim overlay** (Off / Crosshair / Ruler).
Top-down became a latch that remembers the camera it left, since a row of on/off controls should not contain one that only fires.

Two CSS traps on the way: `#controls .action-group:not(:has(.btn:not([hidden])))` hides an empty group, and the new rows are made of `.seg-btn`, not `.btn` - so the whole block was `display: none` while every element inside it reported the right styles.
And at 172px the labels truncated to "Coll…"/"Top…", which is the one thing a button label must not do: the card is 196px now and the rows wrap instead of clipping.

#### "Pro pick" now means what it says

Measured before changing anything: on the nuke target from the report, **45 of 400** lineups carried the badge; on mirage, **72 of 400**.
The test was a 120u origin radius paired with a 220u landing radius, which on a busy map is satisfied by almost any spot near anywhere a pro once threw anything.

Tightened to same spot (64u, plus height within 48u where the demo recorded it) **and** same landing, judged by the Precision filter's own value so the badge means what the list already means by "lands where I asked".
With the default 32u that is **19 of 400** on mirage and **none at all** on the nuke target - which is the honest answer for an arbitrary spot no pro is smoking.

#### The diff shows the surfaces, not squares

Flat quads said "the meshes disagree in this column" and nothing about the shape of what is missing.
`meshdiff` now also exports the mismatched triangles themselves - from the render mesh for surfaces grenades fly through, from the physics mesh for phantom bounces - and the viewer draws those.
On de_nuke: 142,831 render-only and 4,934 physics-only triangles, 6.8MB raw / 605KB gzipped.

That payload is too big to fetch on every map load for an overlay almost nobody switches on, so the viewer now does a HEAD to learn whether a diff exists and fetches the body when the overlay is switched on (which needed `/data/**` to answer HEAD as well as GET).
Diff files with no triangles still fall back to the quads.

#### The sweep grows from three places

A map-wide search floods outward from the target; the spots a player is actually standing on when the round starts were reached last.
It now also floods from the T and CT spawns and orders by whichever front reaches an origin first.
Ordering only - what prunes an origin is still its free-space distance from the zone, the only one of the three that bounds a throw.

Measured on mirage: **within the first 150 origins swept, spots 0u from T spawn and 4u from CT spawn are already done**, alongside the cluster around the target.
Cost: 46.1 / 44.2s against a 46.3s median before the extra fronts - two more floods over the grid do not register next to the sweep.

#### Row detail is behind a hover

The collapsed row is down to the throw itself - which button, how you are standing, and how hard it is - with the execution (movement, aim reference) on the hover of the part of the row that names the throw.
A player scanning a list is choosing between throws, not performing one.

#### Self-inflicted: a deletion that took the scene with it

Removing the per-frame spawn rescale, the edit matched backwards from the wrong comment and also deleted `let activeScene = scene;` two lines above it.
The 3D view then failed with "3D unavailable" and an `Uncaught (in promise)` whose only clue was a stack frame in the `isTextured` getter.
Same lesson as the earlier `git checkout`: bound a deletion by the exact text being removed, never by a search that walks backwards into its neighbours.

### Round: two positions, icon tiles, corner reset (2026-08-30)

#### The card asks two questions, in the order the map answers them

Callout search is gone (it was already parked behind `hidden`), and the first card is now the two positions a solve is made of, each said the same way:

```
Target position   [ setpos -1200 -635 -166 ] [copy]
                  [ ✓ Target set ]
Throw position    [ paste setpos            ] [copy]
                  [ Set throw position ]
```

Each box both reads a position out and takes one in: it mirrors whatever is set (never while it is being typed into), the copy button puts it on the clipboard as a `setpos`, and pasting a `getpos` line and pressing the button below sets it.
The hover on each box says which console command produces the value.

The map takes the two in order - first left click is the target, the second is where you throw from - and either button re-arms its own half out of turn (`state.picking` / `state.pickingOrigin`, Esc cancels either).
A button whose half is answered shows a tick and goes quiet, but stays clickable: a dead control is a trap, and re-picking is the common case.

Each box names the height that belongs to its own half - the resolved floor for the target, the throw spot's own feet for the origin.
Naming the target's floor for a throw spot on another level would hand out a `setpos` that puts the player through it.

#### Word buttons became icon tiles

The view/overlay rows spilled out of the card: a `nowrap` label is wider than its grid track and grid items do not shrink below their content, so "Collision" and "Top-down" ran past the edge.
They are square tiles now - a 16px icon over its own name - three to a row, which cannot be wider than the card whatever the label says. The sidebar went 196px -> 212px.

**The `:has()` trap, for the third time.** `#controls .action-group:not(:has(.btn:not([hidden])))` hides an empty group, and it has now silently hidden a whole block twice: once when the rows became `.seg-btn`, again when they became `.tile`.
Both times every element inside reported correct styles and a zero rect. The rule lists all three classes now.
Same shape of bug in two more places this round: `.place-copy` (`display: grid`) and `.tile-row` (`display: grid`) each beat the UA's `[hidden]` rule, so a hidden copy button and the 2D-irrelevant aim row both stayed on screen. Any class that sets `display` needs its own `[hidden]` rule.

#### Reset moved to the corner

The actions card deliberately has no visible summary, so its reset could not sit in a header row the way the filters and advanced ones do.
It is absolutely positioned in the card's top-right instead, same icon, same size.

#### Search belongs to the throw position, layers belong to the map

"Search the whole map" moved under **Throw position**: it is the other answer to the same question ("this spot, or all of them"), not a section of its own.

That exposed a dead end - re-picking either half left a target and a throw spot on screen with nothing that would put them together again, because the only search on offer ignored the throw spot.
**Search from this spot** now appears whenever both halves are answered and is the primary action; the whole-map search stays beside it and takes over as primary when there is no throw spot.

Heatmap and Pro smokes moved to the bottom-left beside the legend, the way a map keeps satellite/terrain under the map rather than in the tool panel.
Both chips and the legend share one bottom-left bar that grows upward from a common baseline, so opening the legend never shifts the layer buttons.
`#key-3d` keeps its own corner coordinates - only the marker legend joined the bar.

The opened lineup no longer repeats the how-text: the same sentence is the hover on the row above it, in the card as well as in the list.

### Where de_dust2's wall damage went (2026-08-30)

Reported: the wall damage marks used to line up the window and B-door smokes are missing from the textured view. Traced to two separate causes.

**1. Decal quads had no polygon offset (fixed).** The viewer treated a mesh as a decal only when its shader was `csgo_static_overlay.vfx`, and on de_dust2 exactly **two materials** in 706 use it.
The map's actual decal quads - `materials/overlays/wall_stain001`, `materials/decals/hr_decals/dust_panel_paint_patch`, `materials/decals/bombsite_x_spray` - are compiled as ordinary `csgo_lightmappedgeneric` materials that merely happen to be alpha-blended.
Without the offset those quads sit in the same plane as the wall they are painted on and z-fight with it, so a reference someone aims at every round comes and goes with the camera angle.
Decals are now matched by vmat path (`materials/decals/`, `materials/overlays/`) as well as by shader.

**2. Two-layer blend materials lose their second layer (not fixable in glTF).** Most of dust2's wall weathering is not decals at all: it is painted with 2-layer blend materials - a clean layer 1 and a weathered layer 2, mixed by a blend-modulation mask and per-vertex weights.

Measured in `data/de_dust2_textured.glb`:
- **67 of 706 materials** carry a `g_tLayer2Color` that differs from `g_tColor`; every one of them also carries a `g_tBlendModulation` mask.
- Those materials cover **169,866 of 4,408,276 triangles (3.9%)** - and they are the big wall and ground surfaces.
- The glTF material for each keeps **one** `baseColorTexture`: layer 1. The second layer, the mask, and the blend weights are all absent.
- **0 of 760 primitives carry `COLOR_0`**, so the per-vertex blend weight is not in the export either.

This is a limit of the exchange format, not of our pipeline: glTF's PBR material has one base-colour texture, and VRF writes layer 1 into it.
Nothing in the viewer can reconstruct the blend, because two of the three inputs never left the map.
Recovering it would mean writing our own world-mesh exporter that preserves both layers plus the vertex stream, and a custom two-layer shader in the viewer.

Also confirmed while looking: dust2 has **no** overlay/decal entities and **no** `m_infoOverlays` array - its single world node holds 138 scene objects and 379 aggregates, and VRF exports both. Nothing is being dropped at the node level.

### Round: spawn domes and a search scope (2026-08-30)

**Spawns are half-buried domes in team colours**, gold for T and blue for CT, with a ring on the floor around each. Solid geometry, so a click anywhere on one hits it.
A dome sunk to its equator reads as an object occupying the spawn from any angle, where the flat disc it replaced vanished whenever the camera got near ground level.

*Trap:* three.js builds a hemisphere around **+Y**, and this world is Z-up - unrotated, every dome pointed sideways with half of it buried edge-on, which renders as a hollow shell with its near face missing. `geometry.rotateX(Math.PI / 2)` puts the cap up. Translucent domes also sorted badly against the floor they are sunk into, so they are opaque.

**Hovering a spawn on the 2D map now says what clicking it will do** ("T spawn - Select this spawn as your throw position"), and while a position is being picked the spawn wins over a lineup marker sitting on top of it: the question on screen is "which spot", and the markers answer a different one. The 3D view has no equivalent hover - it has no cursor, only a fly camera.

**Search is now a scope, not a button**: `Search: Spot | Map | Spawns`.
Spawns restricts the sweep to stand spots within 256u of any spawn point (`scope: "spawns"` on the query, validated, cache-keyed, QueryVersion 20) - the answer to "what can I throw as the round starts".
Measured on mirage: **2,165 origins instead of 24,477, a cold solve in 6.4s instead of ~46s**, and every one of the 47 results is within 256u of a spawn.

*Self-inflicted, again:* replacing `drawSpawnSet` with a block-bounded edit swallowed `nearestProLanding`, `nearestSpawn` and `SPAWN_GRAB_PX`, which sat between it and the next anchor. The only symptom was a silent `ReferenceError` inside a pointermove handler - no visible error, just a tooltip that never appeared. Third time this shape of mistake has cost a debugging round: bound the replacement by the exact text being replaced, not by "from here to the next landmark".

### Round: honest difficulty, a ruler in real degrees, exact solves (2026-08-30)

#### "Reliable" was being handed to moving throws with nothing to aim at

Reported: a run-jump in the middle of nowhere labelled Reliable.
The old rule was additive - stability plus a pin bonus - so a forgiving run-jump outscored a fussy standing throw, which is backwards: the run-jump asks for a direction, a jump timed against a moving body, and a release, all before the aim counts.

Movement and aim reference are **ceilings** now, not bonuses:
- no run-jump, jump or crouch-jump can be "Easy";
- nothing aimed at a blank wall can be better than "Needs practice", and nothing aimed at open sky better than "Tricky";
- a run-jump with no pin and no edge at the crosshair caps at "Needs practice" however forgiving the numbers are.

Measured over the same 400-lineup mirage solve: run-jumps went from **26 Reliable / 0 Easy** to **14 Reliable / 0 Easy**, with the 12 demoted ones being exactly the case reported - reticle-only reference, no pin. The overall spread stays useful: 76 Easy / 70 Reliable / 167 Needs practice / 87 Tricky.

#### The aim ruler was not in degrees

The lineup ruler placed a tick every 8% of the viewport and labelled them 1..5. At this camera that is **6.84 degrees per division**, so the tick labelled "2" sat 13.7 degrees below the crosshair - which is why comparing it against the game put the same reference on the wrong part of a window.

Ticks are now placed at `tan(angle) / (2 · tan(halfFov))`, rebuilt whenever the viewport changes shape, with fine marks every degree to 5 and coarse ones every five to 40. Verified in the browser: 1 degree lands at 1.16% of viewport height from centre, 5 at 5.83%, 10 at 11.76%.

**The camera FOV was already right** and is not the cause: `verticalFovFromDesired(90)` is 73.74 degrees vertical, which is exactly CS2's Hor+ value for `fov_desired 90`, held constant while the aspect widens the horizontal.

The other half of a UI-vs-game comparison is stance: the preview camera sits at the eye height the lineup is *aimed* from (crouched for crouch and crouch-jump throws), so standing in game to compare a crouched lineup puts the view 18u higher and moves every reference on screen. "Go to" now says which stance it dropped you into.

#### Pasted positions kept their precision, and stopped losing an eye height

A pasted `setpos -2168.963867 1042.062622 103.573364;setang ...` was displayed back as `setpos -2169 1042 40`.
The stored value was always exact - only the box rounded - but a position copied back out was up to half a unit off per axis, which this tool answers questions at 1u.
Boxes show two decimals now, trailing zeros trimmed.

The worse half: `getpos` prints the **eye** position and `setpos` takes the **feet**, so the eye height is subtracted on paste. A position copied out of this tool is already feet, and pasting it back subtracted another 64u every round trip.
The `setang` half of a `getpos` line is the tell for which of the two a paste is, and that is what decides now. Verified: a getpos pastes to feet once, and the value round-trips unchanged.

#### Search scopes: exact, spot, map, spawns

**Spawns** now means the spawn positions themselves, not the walkable ground around them: **33 origins instead of 2,165 on mirage, 0.9s**, and every result's feet are on a spawn. Asking "what can I throw from where the round puts me" should not answer with a spot a few steps away.

**Exact** is new: one origin, the position given, with no lattice neighbour and no pinned variant substituted for it - what someone needs when they paste their own `getpos`, take the angles back into the game, and throw from where they are standing.

Two bugs found building it, both worth remembering:
- The exact origin was handed to the sweep raw, while every other origin goes through `SnapToGround`. Adding the snap then broke it the other way: `SnapToGround` re-derives a floor from the voxel grid, and on this spot it landed somewhere the player hull does not fit, so a position that had just produced three lineups produced none. `ExactOriginOnly` now takes the given position when a player can already stand there and only snaps input that floats.
- The zeros persisted after the fix because the failed answers were **cached**. Same trap as ever: the result cache is keyed on the query, not on the solver, so any solver change needs the affected entries dropped (or QueryVersion bumped) before the next measurement means anything.

#### Wording

"Seen while throwing" is the tag no longer - it reads **Exposed**, with the full sentence on hover.

#### Search buttons latched off after the first solve

`b.disabled = b.disabled || state.busy || state.awaitingLevel` is a latch: nothing ever cleared it, so the first solve (or floor chooser) turned **Map and Spawns off for the rest of the session** - a target set and no way to search from it.
Only Exact and Spot escaped, because a separate line reassigned theirs each pass.
Reproduced by driving `state.busy` through one `syncControls` and watching the flags stay set; the whole row is recomputed from scratch now.

#### The floor chooser asked an unanswerable question on top of the Xbox

Two separate causes, both fixed:

**It collected floors beside the click, not only under it.** `NavGroundLevels` also took any nav area within `NavGapReach` (96u) horizontally - which is right for origin generation, where that reach bridges the slivers between adjacent quads, and wrong for "what is stacked under this pixel". `/api/levels` asks strictly now, falling back to the proximity rule only when nothing contains the point at all.

**And the nav mesh really does draw de_dust2's mid as one polygon that runs under the Xbox**, so a click on the crate is genuinely inside two areas: the crate top at z=36 and the floor sealed beneath it at z=-123. Nothing can be thrown into the second one. Levels with less than 64u of clear space above them (a smoke is 64u across) are dropped, unless that would leave none.

Verified: the column at (-350, 1450) now offers only `Middle (36)`, while genuinely stacked columns keep both floors - `Catwalk (36) / Catwalk (-125)`, `UpperTunnel (36) / UpperTunnel (-109)`.

The chooser's own wording now says which way to lean: "A click from above cannot say which one you meant. If you did not mean something underneath, pick the highest."

### Heights: every position we hand out was low (2026-08-30)

Reported three ways: a 2D click showing `setpos x y 0` where the 3D click showed `-29`, and a spawn-search lineup that teleported the player into the ground.
Ground truth arrived as 15 real de_dust2 T-spawn positions read out of the game, which turned this from an argument into a measurement.

**A 2D click carried no height at all.** The level chooser resolved one only when a column had two or more floors; with one floor it returned without setting anything, so the target kept a 2-element position and the box fell back to `0` - which as a `setpos` is the bottom of the world. Both the target and a 2D-picked throw spot now resolve their floor before either goes near a box.

**Nav heights are not floor heights.** A nav level is the average of a polygon's corners; at de_dust2's T spawn that sits 5u under the real floor. `/api/levels` and the spawn origins now snap onto the collision surface.

**A spawn entity is a marker, not a foot position.** They float - 55u above the floor at T spawn - so solving from one puts the feet in mid-air and the setpos underground. Spawn origins are dropped onto the floor and hull-checked, with the validated stand spot within 32u as a fallback (half of de_dust2's spawns sit on stepped ground where a 32u hull placed on the exact surface point straddles two heights and fails the fit test). Origins went from 14 of 30 usable to 28 of 30.

**And the floor under a point is not the floor under a player.** A single downward ray answers for one point; a player is a 32x32 box resting on the highest thing beneath it. Against the 15 real positions, a centre-only drop was low by **1.18u at the median and 5.27u at worst** - enough to bury whoever pasted it. `LineupSolver.FloorUnderHull` samples the hull's four corners and its centre and takes the highest:

| | centre ray | hull footprint |
|---|---|---|
| median error vs the game | -1.18u | **-0.03u** |
| more than 0.5u below the floor | 9 of 15 | 3 of 15 |

The residual 0.03u is Valve's own feet-above-floor gap, so the copied `setpos` now adds it back: a 2D click at T spawn S2 emits `setpos -367 -808 83.74` against the game's own `83.744965`, **0.005u apart**.

(The three remaining outliers are positions where the reference value has the player standing on something - one is the raw spawn entity height, 30u up in the air.)

**On `setpos_exact`**: it would not have helped. It skips any engine-side adjustment, so it is the less forgiving of the two - `setpos` was never the problem, the height we gave it was. `setpos_player <idx> x y z <drop to ground range>` is the one with an explicit ground snap, worth switching to only if a position is ever uncertain again.

#### Ruler calibration: the reference pose

For comparing our aiming ruler against CS2's own grenade overlay 1:1, the pose is de_dust2's T spawn S2, crouched:

```
setpos_exact -367.000000 -808.000000 83.744965
setang_exact 0.000000 90.077942 0.000000
```

Crouched eye lands at z = 129.785 (feet + 46.04), pitch 0, yaw 90.078, vertical FOV 73.74.
The viewer reproduces it exactly with `__view3d().flyTo({ feet: [-367, -808, 83.744965], type: "Crouch", pitchDeg: 0, yawDeg: 90.077942 })`.

Compare the **vertical** ticks: those follow the vertical FOV alone and are unaffected by the browser stage not being a true 16:9. Compare fractions of viewport height rather than pixels - the stage is 1040px tall inside a 1080p window.

To crouch without holding a key (which collides with the screenshot bind): type `+duck` in the console, close it, take the shot, then `-duck`.

#### CS2's grenade crosshair is 10 degrees per mark - measured, and matched

An in-game 1920x1080 capture of the grenade overlay settled both open questions at once.
Finding every green tick in the image and converting its pixel offset to an angle (assuming CS2's 73.74 degree vertical FOV) gives:

| mark | 1x | 2x | 3x | 4x | 5x |
|---|---|---|---|---|---|
| measured | 10.05 | 20.12 | 30.07 | 40.09 | 50.07 |

and vertically 9.97 / 20.05 / 30.01. Every one within 0.12 degrees of a multiple of ten.

Two conclusions:
- **CS2's marks are exactly 10 degrees apart.** The ruler now draws those as its long marks, named the way the game names them (`1x`..`5x` across, `1y`..`3y` up), with the 1-5 degree marks kept between them at lower contrast for reading a lineup off this screen.
- **Our camera projection is right.** Those angles only come out round if the projection matches CS2's; a wrong FOV would have skewed them. That is an independent confirmation of `verticalFovFromDesired(90)` = 73.74 vertical, from the game itself rather than from a spec.

Verified by measuring our own render the same way, normalised for the 3D stage being 1039px tall inside a 1080px window: **1y 9.99 vs 9.94, 2y 19.99 vs 20.01, 3y 30.01 vs 29.92** - within 0.08 degrees of the game across the board.

The comparison shot does not need matching camera angles, incidentally: the tick geometry depends only on the FOV, so any in-game capture of the overlay calibrates it.

#### Wall and corner spots went invisible, not missing

Reported as "not seeing any corner lineups anymore".
The solver never stopped finding them: the same de_mirage target returns **28 pinned lineups of 400** (27 wall, 1 corner), exactly as before, and 12 of them rank onto page one.

What went was the badge. The card redesign folded the pin into the hover with the rest of the execution detail, on the reasoning that a pin is *why* a throw is easy and belongs in prose - which lost the one class of lineup people go looking for by name.
It is a tag again, in words rather than jargon: **Wall spot** / **Corner spot**, with the reason on hover ("walk into it and your feet are exactly right every time, with nothing to measure").

A **Stand spot** filter joins the others - any / wall or corner only / corner only - because "I want the ones I can walk into" is a way people shop for a lineup, not just a property of one.

(The single corner lineup on that target lands 44.5u out, so the default 32u precision filter hides it. That is the filter doing its job, not a second bug.)

**Lesson worth keeping:** research about what a *new* player needs is not a licence to remove what an experienced one navigates by. The pin belonged in both places - the prose for someone learning what it means, the tag for someone hunting for it.

## Audit 2026-09-02

A seven-pass multi-agent review (security, code quality, performance, test coverage, API + architecture, frontend, devops) against a clean tree at f9b8a59.
38 findings.
Nothing was fixed in this pass; the list below is the queue.

Baseline verified before reviewing: clean build with zero warnings, 162/162 tests, prod running the current image, viewer token `?v=80` matching on both sides, and a real map-wide solve against smoke.npc-server.top returning 400 lineups from 30,633 origins.
A30-C2 (the golden replay test that returned green having asserted nothing) is **fixed**: `tests/Sim.Tests/fixtures/` now carries a cropped de_dust2 mesh plus captures, and the silent skip is a hard `Assert.True(File.Exists(...))`.

Two measurements worth keeping.
A cold map-wide solve on prod takes **102 seconds** wall.
The old ~28-30s figure was taken at 16,148 origins; this ran 30,633, and prod is capped at 6 cores, so this is most likely origin-count growth rather than a code regression - but it is the number a first-time visitor waits.
The solve cache holds ~5.7 MB per entry (measured: 18 entries, 102 MB).

### Open - critical

- **A31-C1 (security)** `src/Cli/Services/LineupApi.cs:446` plus `src/Cli/Commands/ServeCommand.cs:21`.
  Unauthenticated CPU and disk exhaustion via POST /api/lineup.
  The cache key quantizes the target to 0.1u and is not bucketed (deliberate, and correct for accuracy), so `x += 0.1` yields unlimited distinct keys, each a guaranteed cache miss costing ~102s of CPU and ~5.7 MB of disk.
  `SolveGate` is a `SemaphoreSlim(2)`: it caps concurrency, not throughput, and its wait queue is unbounded.
  There is no rate limiting in the app, in docker-compose.yml, or in the traefik labels (verified against prod's actual compose file - only `secureHeaders@file`).
  `PruneCache` runs only at process startup and only deletes .json files older than 30 days, so a long-lived container never prunes at all.
  Confirmed reachable cross-origin: a `Content-Type: text/plain` POST with `Origin: https://evil.example` (a CORS-simple request needing no preflight) was accepted by production and returned 400 lineups, so any page open in a visitor's browser can pin both solve slots.
  Fix, fastest first: a Cloudflare rate-limiting rule on POST /api/lineup (CF already proxies the domain, so this needs no deploy); then `AddRateLimiter` keyed on remote IP - `Microsoft.AspNetCore.RateLimiting` is already in the shared framework under the existing `FrameworkReference`, so it adds no dependency; plus a content-type check and a cache size cap with LRU eviction.

- **A31-C2 (correctness)** `src/Cli/Services/TargetSolver.cs:394` `SnapTargetToGround`.
  The roof-target bug is still live in the deepest fallback.
  The function scans `z = grid.Nz - 2` downward over a probe grid spanning the full mesh Z extent and returns the FIRST empty-over-solid transition, i.e. the topmost surface in the column.
  This is precisely what the comment on `NavGapReach` (`src/Solver/LineupSolver.Origins.cs:669`) describes as the bug that "put de_dust2's BombsiteA and BombsiteB targets ~900u in the air and returned zero lineups for the two most-thrown-at spots on the map".
  The eff2dad fix added 96u nav-sliver bridging so this scan is reached less often; the scan itself was never corrected.
  `NavGroundZ` explicitly does lowest-wins and the sibling `SnapToGround` for origins bounds its scan to a +-8 cell window - `SnapTargetToGround` does neither.
  Reachable whenever a 2D click (no target Z) lands more than 96u from every nav polygon: sparse-nav interiors, exterior ledges, rooftop-adjacent spots on stacked maps.
  Fix: track the lowest match across the scan instead of returning on the first, or bound the scan to a window around an expected height. Small change; needs a test pinning it.

### Open - high

- **A31-H1 (devops)** `Dockerfile:22`.
  The published image silently returns zero lineups by default.
  `CMD ["serve", "--bind", "any"]` omits `--attrs`, and `ServeCommand.cs:161` defaults attrs to `""`, which drops world geometry so every solve returns nothing while the process stays healthy.
  Prod is unaffected only because docker-compose.yml passes the flag; a bare `docker run` of the image, or any compose edit that loses the command, reproduces the project's most-repeated failure mode.
  Fix: bake `--attrs Default,default,EntitySolid` into the CMD and let compose override only what genuinely varies per deploy.

- **A31-H2 (devops)** Dockerfile and docker-compose.yml.
  No HEALTHCHECK, no compose `healthcheck:`, no traefik `loadbalancer.healthcheck` label, and no health endpoint.
  A container serving zero lineups is indistinguishable from a working one, so A31-H1 and every silent-zero regression can only be caught by a human opening the page.
  Fix: a probe that exercises a real solve (a known-good map/target via /api/lineup-one) and asserts a non-empty result, plus a post-deploy smoke test.

- **A31-H3 (api/versioning)** `src/Cli/Services/LineupApi.cs:466` `QueryVersion`.
  Cache invalidation depends on remembering to bump a constant, and the discipline is measurably failing.
  Walking the last 25 commits touching `src/Solver` or LineupApi.cs, seven changed solver semantics with no bump: eff2dad (clicked targets landing on roofs), b97247f (never place an origin in mid air), 0b377a2 (stand players on crates and ledges), cee2cd5 (re-aim verified lineups at the exact target), 9c209f8 (penalize exposed lineups - also added a new `exposed` field), e2dea96 (rank easier throws higher), 6828bf1 (stand the thrower on the floor, not the voxel boundary).
  Each one meant warm cache entries kept serving pre-fix answers until an unrelated later commit incidentally bumped the version.
  Mesh changes are keyed in via the content hash, so this only bites pure-solver commits - which is exactly where the accuracy fixes live.
  Fix: a CI check that fails when `src/Solver/**` or the ranking/serialization block differs without a QueryVersion bump.

- **A31-H4 (test-coverage)** `src/Solver/FreeSpaceReach.cs`.
  The new BFS free-space prune has zero tests anywhere, yet it runs before every sweep (LineupSolver.cs:177) and reorders it via `extraFronts` (line 216).
  An over-prune drops reachable origins with no error - the silent-zero mode again.
  Fix: synthetic-grid tests for an open corridor (free-space distance equals walked length), a solid wall (null despite short straight-line distance - the entire point of the class), an L-corridor, out-of-bounds, a budget shorter than the true path, and two disjoint seed cells.

- **A31-H5 (test-coverage)** `src/Cli/Services/TargetSolver.cs:58` `SolveForTarget`.
  The single function every /api/lineup request runs through has no test that calls it; nothing in tests/ references `TargetSolver` outside a comment.
  It is where spawnsOnly, exactOrigin, DropToFloor, NearestStandSpot, the crouch filter, and the LOS/pin post-processing are wired together - the exact multi-branch integration where the dust2 BombsiteA/B, de_vertigo, and floating-spawn bugs all lived.
  Also untested: `FloorUnderHull` (`LineupSolver.Origins.cs:424`), whose own comment documents the median 1.2u / max 5.3u error that teleported people into the ground, and `ExactOriginOnly` (line 449).

- **A31-H6 (api/error-handling)** `src/Cli/Services/LineupApi.cs:610`.
  An empty result is always HTTP 200 with `lineups: []` and no machine-readable reason, so a genuine bug is indistinguishable from a legitimately unreachable target.
  `TargetSolver.cs:172` already detects the specific "target is inside solid, or tolerance too small" case and writes it to the server's stderr, then falls through and runs the full sweep anyway - the explanation exists and never reaches the client.
  Fix: classify the empty case (no reachable origins vs origins found but nothing verified vs target resolved off nav data) into an `emptyReason` field, and return early when the zone is empty rather than sweeping for nothing.

- **A31-H7 (devops)** docker-compose.yml:11 and :8.
  Prod pulls the mutable `:latest` tag and nothing records the deployed sha, so rolling back a bad ship means digging through GHCR under pressure.
  The same file also declares `build:` alongside `image:`, so `docker compose up -d --build` - a natural command to reach for while troubleshooting - would build from prod's known-stale, dirty checkout and bypass CI entirely with no signal.
  Fix: pin SMOKESOLVER_IMAGE to the sha tag CI already publishes and write it to a file on the host at deploy; drop `build:` from the prod-facing compose into an override file.

### Open - medium

- **A31-M1 (security)** ServeCommand.cs:394, :428, :460.
  /api/trajectory, /api/lineup-one and /api/slack run CPU-bound physics synchronously with no gate at all, unlike POST /api/lineup.
  /api/slack runs up to 84 simulations per request, and none of the three bound coordinates to the map AABB, so aiming into open sky forces the full 640-tick worst case cheaply.
- **A31-M2 (api)** ServeCommand.cs:495. No Content-Type check on POST /api/lineup - see A31-C1 for the confirmed cross-origin consequence.
- **A31-M3 (api)** ServeCommand.cs:419. /api/trajectory, /api/lineup-one and /api/slack set an ETag and a 7-day CacheControl but never call the `IsNotModified` helper that /api/mesh uses three lines away, so `If-None-Match` always gets a full recomputed body instead of a 304.
- **A31-M4 (frontend)** viewer/js/map2d.js:658. Every native pointermove runs `nearestLineup()`, which re-runs `filtered()` (a full filter with ~8 predicates per lineup) plus a distance check over the survivors, unthrottled; only the redraw is rAF-coalesced. Janky hover on large map-wide results.
- **A31-M5 (frontend)** viewer/js/main.js:405. Collision, Top-down, Mesh diff, Spawns, Pro smokes and the pro-smokes segment toggle only a CSS `.active` class and never set `aria-pressed`, unlike the View row and aim-overlay row directly above them.
- **A31-M6 (architecture)** ServeCommand.cs:42-152. `SpawnCache`/`PlaceCache`/`LoadSpawns`/`LoadPlaces` re-implement, in the routing file, the same per-map lazy-load-and-cache idiom `MapRegistry` already owns for maps and stand spots.
- **A31-M7 (performance)** LineupSolver.Origins.cs:364. `AddPinnedOrigins` walks every stand spot single-threaded (16 raycasts each, ~250k serial raycasts on a map-wide query) while the other cores idle, immediately before the fully-parallel sweep. Estimated 3-4% of the cold solve; unprofiled, and the shared `seen`/`pinned` mutation needs thread-safety work as part of any fix.
- **A31-M8 (test-coverage)** `RunTargetQuery` (LineupApi.cs:488) is untested despite LineupApiTests covering the two pure functions beside it, and the `broken` world-state tests only exercise the exact collider, never the voxel grid the sweep actually prunes against - so a regression in the grid's attribute filter would silently zero every broken-glass query.
- **A31-M9 (supply-chain)** Dockerfile:2 and :16 pin base images to the floating `10.0` tag; .github/workflows/ci.yml pins every action to a floating major (`actions/checkout@v4`, `docker/build-push-action@v6`, ...) while the publish job holds a `packages: write` token that prod auto-pulls.
- **A31-M10 (ci)** .github/workflows/ci.yml has no `concurrency:` group, so two close pushes can race and leave `:latest` pointing at the older commit.

### Open - low

- **A31-L1 (correctness)** LineupSolver.cs:250. `zoneRise` always subtracts `CrouchEyeHeight` regardless of throw type, inflating it by up to 18.02u for standing types and eating into the 128u `VerticalReachMargin` cushion. No observed miss (the margin is 7x the error), but it silently narrows the intended safety band. Use `EyeHeight(type)`, as line 281 already does.
- **A31-L2 (error-handling)** ServeCommand.cs:574. The NDJSON writer's bare `catch` sets `clientGone = true` and logs nothing, so a serialization or full-disk failure is misdiagnosed as a disconnect and leaves no trace. Catch the disconnect types specifically and log the rest.
- **A31-L3 (frontend)** viewer/js/view3d.js:243. `scene.add(phantomVisual)` runs unconditionally but `phantomVisual` is only built when `phantomICount > 0`; three.js r144 logs a console error rather than throwing, so maps with door/glass geometry but no phantom clips spam the console on every 3D load.
- **A31-L4 (ci)** The build-and-test job declares no `permissions:` block, inheriting whatever the repo default is.
- **A31-L5 (scripts)** rig/deploy-plugin.sh:16 discards all rsync stderr before falling back to `cp`, hiding genuine failures (permission denied, disk full) as if rsync were merely absent.
- **A31-L6 (housekeeping)** `data/onboard-logs/` (62 MB) and `data/reextract-logs/` are untracked and not gitignored, so they are one `git add -A` away from the repo.

### Assessed and explicitly rejected

- **The "per-origin sweep table, 10-20x" idea should be dropped.**
  It is not implemented anywhere, and the codebase's own evidence argues it would be unsound.
  `RestScatter` and VerifyExact's position-chaos probe exist precisely because real-world validation showed the rest point can move hundreds of units when the feet shift by a single 0.25u movement tick.
  A table sharing or interpolating trajectory outcomes between origins 16-32u apart would silently misclassify exactly the bounce-boundary throws that mechanism was built to catch.
  Treat the 10-20x figure the way the "expired trajectories dominate tick work" claim turned out: unverified and probably wrong until someone produces a mechanism and a controlled experiment.

### Checked and clean

Worth recording so the next audit does not re-derive them.
Path traversal on static serving and /data/** (tested live against a running server: dot-segments, URL-encoded variants, encoded slashes, case variation, null bytes all rejected; the previously-fixed allowlist and cache `--root` bugs both hold), SSRF, deserialization, command injection, and secrets in CI.
The viewer came back clean on every known-regression pattern: `?v=80` consistent across all script/link tags and all inter-module imports, no revived `disabled ||` latch, the `:has()` group rule at app.css:190 correctly lists `.btn`/`.seg-btn`/`.tile`, no deleted-but-referenced identifiers, `mapGeneration` checked after every await, and every `innerHTML` interpolation of server or user data passes through `esc()`.
three.js disposal is handled deliberately throughout (`clearGroup`, `disposeSceneContents`, `teardown3d`'s resize listener removal, flycam's paired start/stop).
`.dockerignore` correctly excludes the 2.8 GB data/ directory from the build context, and CI does gate publish on tests via `needs: build-and-test`.

### Reference quality: the product problem, measured

From the same live prod solve (de_dust2, 400 lineups), which quantifies the "most lineups are useless without a good point of reference" complaint:

| aim tier | count | median off crosshair |
|---|---|---|
| edge | 168 | 2.12 deg |
| reticle | 211 | 20.28 deg |
| flat | 21 | - |

Only **109 of 400** are edge tier within 3 degrees - an actual "put your crosshair on that corner" lineup - and only **9** of those sit on a wall or corner pin.
378 of 400 are aiming at sky.
The ranking still surfaces 20-degree-off throws alongside the good ones, which is the Reproducible First plan's premise, restated in fresh numbers.

### Audit 2026-09-02: what was fixed

Everything below was fixed the same day and verified: clean build, 198/198 tests (24 of them new), a real Docker image build, and live checks against a running server.

**A31-C1 (critical, security) - unauthenticated CPU and disk exhaustion. Fixed.**
Three defences, because the hole had three parts.
`POST /api/lineup` now requires `Content-Type: application/json` and answers 415 otherwise, which closes the cross-origin drive-by: a browser can send text/plain, form-encoded, or multipart across origins without a preflight, but not JSON.
A per-IP token bucket (`AddRateLimiter`, 20 burst then 10 per 30s, keyed on X-Forwarded-For behind the proxy) makes walking the cache key space pointless; the physics GETs got their own, roomier bucket (240 burst / 120 per 30s) since they were previously ungated entirely while the solve they compete with for CPU was capped at two.
`SolveGate`'s wait queue is now bounded at 16, answering 429 rather than growing without limit.
The solve cache gained a running 4 GB ceiling with oldest-first eviction (`MapRegistry.EnforceCacheBudget`), checked after every 256 MB written - the startup-only 30-day pruner never ran at all on a long-lived container.
Verified live: text/plain and form-encoded now 415; 17 requests through then sustained 429s; a second burst of 30 entirely refused.

**A31-C2 (critical, correctness) - the roof-target bug was still live. Fixed.**
`SnapTargetToGround` scanned down from the sky and returned the first floor it met, which is the roof - the same top-down geometry scan `NavGapReach`'s own comment blames for putting de_dust2's BombsiteA and BombsiteB ~900u in the air. The 96u nav-sliver bridging added in eff2dad narrowed how often it is reached but never fixed it.
It now takes the nearest walkable area at any distance as an anchor and picks the surface closest to it, falling back to lowest-wins with no anchor - matching `NavGroundZ`'s stacked-areas rule.
`SnapTargetToGroundTests` pins it: against the old scan 4 of its 8 cases fail, the headline one reporting a target resolved 528u above the floor.

**A31-H1/H2 - the image shipped a silent-zero default, and nothing could detect it. Fixed.**
The Dockerfile CMD now carries `--attrs Default,default,EntitySolid`, so the published image is correct without compose.
A new `selfcheck` command answers "would a solve return anything?" - maps present, nav data present, geometry surviving the attribute filter, and a drop test proving the collider really sees the world - and is wired as a HEALTHCHECK in both the Dockerfile and compose.
It deliberately loads ONE map, not all of them: the obvious `LoadMaps` implementation peaked at 2 GB RSS, which is the container's entire allowance, so the healthcheck would have OOM-killed the server it was checking. One map runs in 0.39s at 72 MB.
Verified in the real image: exit 0 healthy, exit 1 on empty attrs, exit 1 with no data volume.

**A31-H3 - QueryVersion discipline. Fixed with a CI gate.**
`rig/check-query-version.sh` fails a PR that touches `src/Solver`, `src/Sim`, `LineupApi.cs`, or `TargetSolver.cs` without bumping the constant, and is wired into the workflow. Verified both directions: exit 1 when the bump is missing, exit 0 when present.
QueryVersion went 20 -> 22 across this work (the roof fix changes target resolution; `emptyReason` changes the response shape).

**A31-H6 - empty results were indistinguishable from bugs. Fixed.**
`TargetSolve` carries an `EmptyReason`, surfaced as `emptyReason` in the API and shown by the viewer in place of its guess. It separates "target resolved inside solid geometry" from "no stand spots in range" from "none of the N spots in range can land a smoke there".
The inside-solid case now returns immediately instead of sweeping every origin to reach the empty list it already had - minutes of CPU for a knowably empty answer.

**A31-H7, M9, M10, L4 - deploy and supply chain. Fixed.**
Base images pinned by digest; all five actions pinned to commit SHAs with their version in a trailing comment; a `concurrency` group so two pushes cannot leave `:latest` on the older commit; an explicit `permissions: contents: read` on build-and-test.
`build:` moved out of the prod compose into `docker-compose.build.yml`, so `up -d --build` on the prod host can no longer deploy its stale checkout behind CI's back.
The watchtower label prod carried as an undocumented local edit is now in the repo, ending that drift; how to pin `SMOKESOLVER_IMAGE` to a sha for rollback is documented beside it.

**A31-M3, M4, M5, M6, M8, L2, L3, L5, L6 - fixed.**
The three physics endpoints answer `If-None-Match` with 304 (verified live) instead of setting an ETag nobody honoured.
`filtered()` is memoized on a signature of every input it reads, so the 2D map's per-pointermove hit test stops re-filtering hundreds of lineups.
A single `press()` helper sets `aria-pressed` alongside the `.active` class on every stateful toggle.
`scene.add(phantomVisual)` is guarded and hoisted out of the loop it was running in per door/glass group.
Validator tests now cover `scope` and `broken`; a new test proves breaking glass opens it in the SWEEP's voxel grid, not only in the exact collider.
The spawn/place caches moved to `MapRegistry` beside the sibling per-map loaders.
The NDJSON writer's bare `catch` now logs anything that is not a disconnect.
`rig/deploy-plugin.sh` no longer discards rsync's stderr; the onboard/reextract log directories are gitignored.

**A31-L1 - fixed.** `zoneRise` moved inside the per-type loop and uses `EyeHeight(type)`, instead of charging every throw the crouch height and overstating the climb by 18u for standing types.

### Falsified by measurement - do not revisit without new evidence

**A31-M7 (parallelize AddPinnedOrigins) is not worth doing.**
The claim was 3-4% of a cold solve, explicitly unprofiled. Measured from the NDJSON phase markers on a real de_dust2 map-wide solve (13,271 origins): the entire `prepare` phase - grid build, origin generation AND pin probing together - is **0.40s of 19.11s, 2.1%**, and pin probing is only a part of that. The estimate exceeded the whole phase it sits in.
The change would require replacing a `HashSet` and `List` with concurrent structures inside origin generation, which is where this project's silent lineup-loss bugs have historically lived, to win under 0.4s. Rejected.
Phase split for the record: prepare 0.40s, sweep 17.76s, verify 0.86s - consistent with the established ~92% sweep figure.

### Still open

- **A31-H4/H5 partially addressed.** `FreeSpaceReach` now has 7 tests covering the property the prune rests on (a wall is not crossed even where the straight line is short, budget refusal, multi-seed, solid-seed containment). `SnapTargetToGround` and the cache budget are covered. Still untested: `SolveForTarget` end to end, `FloorUnderHull`, `ExactOriginOnly`, and `RunTargetQuery`.
- **A31-M1 partially addressed.** The physics GETs are rate-limited now but still not bounds-checked against the map AABB, so a throw aimed into open sky still runs the full 640-tick budget.
- **A31-H3 (Dockerfile USER) deliberately NOT shipped.** `data/cache` on the prod host is owned by root:root because the container created it while running as root, so adding `USER $APP_UID` would make every cache write fail on the next `compose up`. The Dockerfile carries a comment with the exact chown that must happen first. Do the chown and the USER line together, in that order.
- **A31-L4 (viewer smoke test) not automated.** A headless-Chrome pass was run by hand this session (page loads, zero JS errors, all overlay tiles carry aria-pressed, all 25 asset tokens on ?v=81) but nothing runs it in CI.
- **Cloudflare rate-limiting rule not created.** The in-app limiter is the durable fix and is live in code; a CF rule on `POST /api/lineup` would shed that load at the edge before it reaches the box, and needs dashboard access.

### New finding, not in the original 38

`viewer/validation.html` loaded the SAME `app.css` as `index.html` under a different cache token (`?v=56` against `?v=81`) and its own script at `?v=3`. The frontend pass checked `index.html` and `viewer/js/**` and reported the tokens consistent, which they were - within that set. Any cache holding those entries served the validation page a stylesheet from many revisions ago, and no app.css change would ever have busted it. All 25 tokens across `viewer/` are now on one value.

### Deployed 2026-09-02, and the bug the deploy verification caught

Shipped as 47abaa1 (the audit fixes) and cd4af7a (the follow-up below). Prod runs the new image, `health=healthy`, attribute filter correct, viewer on ?v=81, 16 maps, solves returning 400 lineups.

**The first rate limiter could be bypassed completely, and only testing production found it.**
`ClientKey` keyed on X-Forwarded-For's first entry. Cloudflare and traefik APPEND to that header rather than replacing it, so a value the client sends arrives at the front of the chain and the limiter reads attacker-controlled text as the caller's identity - a fresh header per request meant a fresh bucket per request.
Measured against the freshly deployed prod: the real bucket was exhausted to 429, then two forged X-Forwarded-For values both went straight through with 400.
Fixed by keying on CF-Connecting-IP, which Cloudflare writes on every proxied request and overwrites whatever the client sent, falling back to the socket address with no Cloudflare in front (which shares one bucket across a proxy - over-limiting, the safe direction).
Re-verified after deploying the fix: bucket exhausted, then 7 of 8 distinct forged X-Forwarded-For values refused with 429 (the one pass being a legitimately refilled token at 10 per 30s). Cloudflare additionally rejects a forged CF-Connecting-IP with its own 403 before the request ever reaches the origin.
`ClientKeyTests` pins all four cases. 202 tests.

The lesson worth keeping: a rate limiter's identity source has to be a header the client cannot write. "The proxy sets X-Forwarded-For" is true and useless - the question is whether the proxy REPLACES it, and both hops here append. This was reasoned about correctly-sounding and was wrong; the ten-second experiment against the real deployment settled it.

Rollback point if needed: the image running before this deploy was
`ghcr.io/nc1107/cs2-smoke-solver@sha256:68acc290f7752494efafa641d44132b206376e46c5fbab26a0470bb7cc071662`
(set `SMOKESOLVER_IMAGE` to it in prod's .env and `docker compose up -d`).


## Stage 1 of Reproducible First, plus smoke coverage (2026-09-02)

### Ranking and describing by reproducibility

`AimReferenceInfo.Band` grades the aim margin that was previously computed and discarded: 0 = a silhouette within 1 degree of the crosshair, 1 = within 3, 2 = within 6, 3/4 = out on a reticle arm, 5 = a blank wall, 6 = nothing at all.
That band now leads the ranking, with position chaos folded in as a three-band penalty rather than a separate sort key - a throw whose rest point jumps when the feet move one tick is not reproducible either.
Measured on a de_dust2 map-wide solve: the top 20 became entirely band 0, led by a corner-wedged throw with a 0-degree reference.

A `reference` filter ships defaulted to "on the crosshair" (band <= 1, edge within 3 degrees) and shows 163 of 400 on that solve.
When it would empty the list entirely the viewer shows everything anyway and says so - hiding a target's only answer is worse than qualifying it.

### Wall contact was binary and misleading

`PositionPin` measured a gap and threw it away, so "wedged in a corner", "flush against a wall" and "a shoulder's width off that same wall" were indistinguishable - the third showed nothing at all.
Measured: **110 of 400 lineups sat within 40u of a wall without touching it, every one of them displaying no wall information whatsoever.**
`PositionStance` now returns the signed gap from the hull FACE to the nearest wall plane, and the row says which of three things it is: `Wedge into corner`, `Walk into wall`, or `12u off the wall`.
The notice range is 16u (a hull half-width) because past that the gap is plainly visible and a badge would be noise; open ground stays untagged since it is 323 of 400.

**Hull validity, checked rather than assumed.** Pinned origins run through `StandSpots.StanceAt`, a real 32x32x72 box test.
Measured across 400 lineups: worst overlap 0.57u against a 0.5u skin allowance - 0.07u of plane-fitting noise on a 16u grid, not a player pushed into geometry - and zero pins claiming contact they do not have.
`StanceGeometryTests` pins both failure directions.

### Difficulty stopped calling movement throws reliable

Airborne throws (jump, crouch-jump, run-jump) reached "Reliable" on stability alone, which measures only how far the crosshair may drift and says nothing about landing a jump.
They now cap at "Needs practice" and must EARN "Reliable" with pinned feet AND an edge within 3 degrees - the two things that actually go wrong.
Airborne throws reading Reliable or Easy: **96 -> 6**, all six pinned with a tight reference. "Easy" is now stationary-only.
The match score also moved onto every row; it had been two clicks deep behind a "Show details" disclosure inside an opened card, so the list could not be compared on the number it was ordered by.

### Smoke coverage overlay

New `/api/smoke`, and a Coverage tile in the overlays row: the volume a smoke landing on the current target would actually fill, flooded through the map's real geometry by the same `SmokeFloodFill` the solver uses.
Not a circle - a circle promises coverage through walls, which is the one thing the overlay exists to check.

Researched rather than guessed.
The real grenade is 288 units across (144 radius), shipped in CS:GO and kept in CS2; a third-party CS2 reimplementation confirms Valve's own approach is a voxel grid plus a "limited flood fill" that fills spaces and adapts to geometry without leaking - the same shape as this model.
Those numbers are now named constants (`SmokeParams.GameRadius`, `CoverageRadius`, `GameCellBudget`) instead of a bare 144 in the viewer and a 165 in the solver.

The overlay draws at **128u, about 89% of the real reach**, so the area shown is area to count on rather than the grenade's absolute best case; the response also carries `fullRadius` so the optimistic edge can be drawn later.

Verified geometry-awareness on four de_dust2 positions - the model reproduces real smoke behaviour:

| position | cells | footprint | height |
|---|---|---|---|
| A site (open) | 1750 | 50,432 | 240u |
| mid (open) | 1443 | 46,848 | 240u |
| T spawn (low ceiling) | 1227 | 43,776 | 160u |
| long doors (confined) | 889 | 31,744 | **272u** |

Confined space gives the smallest footprint and the tallest column - the smoke climbs instead of spreading, exactly as it does in game.

**Left as it was, deliberately:** `SmokeParams.UncalibratedDefault` is still 165u.
Its own name says nobody has validated it, it drives the older CLI solve paths (`solve`, `lineups`, `LandingZoneSolver`), and retuning a physics constant underneath them as a side effect of a UI feature is how silent accuracy regressions happen.
It wants its own calibration pass against the game.

**Verification gap worth naming:** the 2D overlay was confirmed to paint by measuring the canvas before and after toggling; the 3D instanced volume was verified by code path, module parse and leak-safe disposal only, not seen rendered.
The headless browser became unusable partway through - 87 orphaned Chrome processes from repeated sessions had taken the machine to 22 of 30 GB.

QueryVersion 22 -> 26 across this work. Viewer token 81 -> 86. 210 tests, 18 new.


## Executes (2026-09-02, later)

Two endpoints, because an execute has two questions and only one of them was
answerable before.

**`/api/execute`** - from THIS spot, solve these smokes. Origin-scoped on
purpose: measured, a map-wide solve is 60-90s while a solve from a fixed spot is
2.3s at 64u reach and 6.4s at 200u, which is what makes several in one request
reasonable. Six smokes maximum, best eight throws each, and the pre-trim count
rides along so the viewer can say "best 8 of 45" instead of implying 8 was all
there was. Each smoke goes through the ordinary origin-scoped query path rather
than a parallel implementation free to drift from it.

**`/api/execute/spots`** - where can I stand to throw ALL of these? This is the
question a player building an execute actually starts from; the first endpoint
answers the follow-up once the spot is known. It runs a full map-wide solve per
target (cached like any other search, so the second execute over the same site
is nearly free - measured 0.09s warm) and intersects the per-target answers
spatially. The solve gate is taken and released PER TARGET rather than held for
the whole request: four targets would otherwise lock every other user out for
the best part of ten minutes.

Ranking is by the WORST smoke in the set, not the average. An execute carrying
one throw nobody can reproduce is not an execute however easy the other three
are, and a mean would hide exactly that behind them.

Verified on the real case. "Where can I stand to smoke B doors and the hole on
de_dust2" returns 12 distinct places, best first, including one that throws B
doors from a standing position. From the spot it names, `/api/execute` produces
both throws with console commands in 7 seconds.

**A bug this found in itself.** The first version deduplicated candidate stances
by rounding feet into cells, and returned spots 16u apart as separate answers -
twelve rows describing about five actual places. Cell boundaries split
neighbours. It now thins greedily over the RANKED list (keep the best, refuse
anything within `within` of one already kept), which is boundary-free and keeps
the best member of each cluster. Re-measured: 12 distinct spots, closest pair
116u apart. Both behaviours are pinned by tests.

The disk solve cache moved into three shared helpers (`SolveCachePath`,
`ReadSolveCacheAsync`, `WriteSolveCacheAsync`) so the streaming lineup endpoint
and both execute endpoints cannot drift on the write-to-temp-then-rename rule -
the thing that stops a kill mid-write leaving a truncated file that a later hit
splices into its NDJSON stream as garbage.

### Still missing

No viewer for either endpoint yet: both are reachable only by API. The natural
UI is pin a position, add targets, and show the smokes in order with their
combined coverage - the coverage overlay is already the other half of "does my
execute actually cover the site".

237 tests.

## Beta readiness pass (2026-09-03)

Everything below is live at smoke.npc-server.top (viewer `?v=101`, QueryVersion 31).
Executes have their viewer now (their own card, five smokes, named by pin), and the "still missing" note above is closed.

### The solver bug Nick found, and where else it lived

A real getpos taken wedged against a crate on de_dust2's A site came back "no stand spots in range".
The exact-origin paths grounded a click with a single ray under the column centre, but the player hull rests on the highest floor point under its whole 32x32 footprint.
On a 6% slope those differ by about a unit, more than the hull test's 0.5u skin, so the click sat "inside the floor" and was rejected.
Every place that puts feet on the floor was then audited:

| Path | Grounding | Verdict |
|---|---|---|
| exact click (`ExactOriginOnly`, `ExactOriginWithPins`) | centre ray | bug, fixed (`HullRestHeight`) |
| wall and corner pins (`AddPinnedOrigins`) | centre ray, then hull test | bug, fixed - every wedge against a crate or low wall on sloped ground was dropped |
| precomputed stand spots | hull-footprint sweep | fine |
| spawn drop (`FloorUnderHull`) | five rays, highest wins | fine |
| lattice fallback (`OnSurface`) | centre ray, no hull test after | harmless, feet ~1u low |

Measured map-wide on dust2 to BombsiteA: pinned origins 931 -> 2000, corner wedges 49 -> 110, pinned lineups in the top 400 doubled.
Both fixes have regression tests on a synthetic 6% slope, and the pin test was confirmed to fail on the old code.

### Exact-spot solves are now the exhaustive search

One candidate per 8u bucket meant the whole "from exactly here" answer rode on a single throw, and when that one failed verification the spot reported nothing.
An exact solve now keeps a candidate per throw kind, uses a 0.25 degree lattice, refines every near miss, opens the verify gate to its floor, and - when the voxel sweep still finds nothing - runs the exact simulator over every angle of every kind (~144k flights, ~22s) before saying no.
Spot searches keep a candidate per kind at the clicked spot and its pins too, and honour a pasted Z over the nav mesh.
A 2D click with no Z takes the lowest stand spot within a hull width, so a click at the foot of a crate no longer lands on top of it.

### Smoke coverage as a fixed amount of gas

The flood fill was a hard sphere and stopped in mid doors where the game keeps going.
It is nearest-first through free space now, with the cells the radius holds in the open as its budget: identical in the open, and boxed in it spends the cells the walls took away further along (to 1.75x the radius), with cells below the landing counting as nearer so it pours off cat.
Mid doors on dust2: 977 -> 1153 cells, 76 of them past 128u.
This is a heuristic and has not been calibrated against the game's volumetric smoke; that needs a rig capture of smoke extents, which the validation plugin does not record yet.

### Viewer

Named targets are an overlay: pins with name plates in 3D, a Targets tile (persisted), a click on a pin in 2D or 3D makes it the target whatever else a click would have meant.
Duplicate provisional names are numbered until a person names them.
Saved view (Results | Saved), Steam persona and avatar, execute card, panel header in two rows with the chevron on the right, votes on the selected card reaching the server, and the collapse arrow moved.
Spawn markers dropped to the floor; the textured GLB's light_environment (a 1707-intensity sun that turned lit overlays white) stripped on load.
Two module-level consts used by a `?t=` permalink during boot were declared below their first use - a silent ReferenceError on every shared link - and are hoisted.
`rig/check-viewer.mjs` runs in CI: modules parse, every `getElementById` names a real id, `?v=` tokens agree, divs balance.

### Warmed for the first click

`rig/warm-cache.py` now solves every named pin first; the seven maps with pro data were warmed and the cache rsynced to prod, so a beta tester's first click on a pin answers from disk instead of a 40-100s cold solve.

### Known limits going into beta

- de_train, de_vertigo, de_cache, de_boulder and the cs_ maps have no named pins: HLTV now sits behind a Cloudflare challenge that `rig/gather-pro-demos.sh` cannot pass, and `rig/gather-hltv-browser.py` needs a human to pass it once in a headed Chrome.
- de_boulder has no textured view (upstream exporter crash); the tile is hidden there.
- Cold map-wide solves off the warmed pins are still 40-100s.
- Provisional names are still provisional; the naming pass is Nick's.
- No execute scoring, no noise (audibility) filter.

284 tests.

## The A-site test bench (2026-09-03, later)

Nick's bench: target at the centre of de_dust2's A site (`setpos_exact 1130.38 2504.53 95.75`), and nine positions he calls reasonable lineups - every one a corner or wall around the site, most of them blind lobs.
`scratchpad/bench.mjs` runs it; keep a copy of the reference positions in this section if the scratchpad is gone:
1069.05/2348.03, 1235.97/2348.05, 1235.96/2460.91, 1069.03/2411.97, 1101.04/2569.63, 1235.97/2561.04, 1300.04/2446.28 (z 54), 1300.03/2342.97 (z 22), 1004.97/2379.97 (z 21) - getpos eye heights, feet are 64u lower.

What it found, in order of discovery:

- **Ranking led by aim band put every corner lob below every long referenced throw.** A blind lob from a corner 150u out misses by a handful of units; a pinpoint aim from open ground 1500u out misses by more. `HumanError` (server and viewer) estimates the miss a person adds - feet by pin, aim by band scaled by flight distance, movement, chaos - and leads the ranking in 8u bands. The viewer filters on it (Reproducible, 32u default), orders by it, and derives the difficulty word from it. Aim-reference and sky filters now default to any.
- **One candidate per 64u sweep cell, chosen by bounces, was pin-blind.** The corner wedge lost its cell to the open ground beside it. Pinned origins keep their own bucket now.
- **Knee-high walls were invisible.** The wall probes sat at 36u and 46u; the site is ringed with walls and crates about 28u tall, so seven of the nine positions were "open ground". A 22u probe sees them, kept only when the hull cannot climb what it hit (floor measured short of the face, hull pushed into it and lifted one step), which rules out stair risers.

Before: 3/9 reference positions appeared as lineups within 12u, result pins 25 corner / 340 wall / 35 open, nearest lineup to the target 234u.
After: 7/9, 33 / 355 / 12, and the crate wedge Nick stood in ranks first with the top ten all corners and walls at the site.
The two still missing are along walls where the pins sit 23-29u from his spot - the same wall, a different point along it.

Open: the numbers in `HumanError` (2/8/24u, 0.5-5 degrees, 6/16u) are judgement, not measurement. The rig can measure them: throw the same lineup from feet placed by hand N times and take the spread.

## Phantom bounces and the corpus replay (2026-09-04)

Nick asked for the phantom-bounce sites to be fixed.
The July list of sites turned out to be stale (x_box, revalidated 30 Aug, had 1 miss in 60), so the work became building the instrument that says where the tail is now.

Two new CLI commands:

- `diverge --geo --report [--index]` replays each miss of a validation report tick by tick against the rig's capture, pairs every sim bounce with the nearest real one, names the triangle (attribute, size, corner) the sim bounced off, fits the normal our own bounce model would need to reproduce the real rebound, and prints the split tick.
- `replay --geo [--reports] [--worst N] [--moved N] [--nonsolid layers] [--nonsolid-groups i] [--no-edge-tip] [--sphere] [--face-normals]` re-simulates every graded throw in the reports for a map from its recorded launch state and scores the rest against the real one: 10,870 throws in under a minute, no game needed.
  This is the benchmark every physics or extraction change should be run through before it ships.

What the corpus said, and what shipped:

- **Edge tipping was net harmful.** Fixed 13 throws, broke 50 (pole tops, crate rims, the mid-doors beam). `ThrowConstants.EdgeTipping` now defaults to false; the test that asserted tipping now asserts balance.
- **`prop_dynamic` hulls (added 30 Aug) were the whole of the recent regression.** 48 throws on de_nuke fell through the open vent slats; 83 on de_mirage. Removed from `SolidEntityClasses`. Neither map had been revalidated after the re-extraction, which is how it went unnoticed for five days.
- **Surfaces that exclude `csgo_thrown_grenade` are air to grenades.** The heaven railing on de_nuke (`passbullets`, 22 throws straight through) carried the flag in `m_InteractExcludeStrings`; an identical-looking `passbullets` grate floor did not, and stopped every throw. The `.s2geo` format is now V3 with a per-group exclude table (V2 still loads, as "excludes nothing"), `GrenadeSolidFilter` honours it, and `PlayerSolidFilter` honours `exclude=[player]` the same way.
  Every map carries such groups; all 15 meshes were re-extracted.
- **Falsified, with numbers:** a sphere hull instead of the box (worse on every map, dust2 26.6% over 8u); preferring face normals when an edge axis wins a near tie (dust2 325 to 350 misses); treating all `passbullets` as air (nuke 72 to 118).

Corpus, over 8u, before and after (validated maps): dust2 325 to 289, nuke 72 to 4, inferno 11 to 9, mirage 127 to 34, overpass 19 to 16, ancient 43 to 28, anubis 13 to 6, office 12 to 0.
Overall 622 to 386 of 10,870 (5.7% to 3.6%).

What the replays show is left in the tail:

- de_dust2 CT-mid slope (`[-328,2424]`, ~50 July throws): flights track to 1u through six bounces, then the sim's last low-speed bounce order differs from the game at a wall-floor corner.
- Bumpy terrain on de_inferno: sim and real bounce at the same tick within 1u, but the rebound implies a different triangle normal than ours, in both directions. The physics mesh is what the game has; why the game's contact normal differs at near-coplanar triangle edges is unresolved.
- de_dust2 B site and top_window rests: settle differences after tracked flights.

Deploy note: `.s2geo` and `.standspots.json` are gitignored data; both must be rsynced to prod (the stand spots were regenerated because the player collider changed).

### Loop iteration 1 (2026-09-04): two contacts per tick

The dust2 CT-mid traces showed the game reflecting off the floor and then the wall of a corner inside one tick, where the sim resolved only the floor and let the wall stop the hull until the next tick.
`ThrowConstants.BouncesPerTick` now defaults to 2 (3 adds nothing).
Corpus: dust2 289 to 284 (5 fixed, 1 broke), nuke 4 to 3, six other maps unchanged; total 386 to 380.

### Loop iteration 2 (2026-09-04): rest on faces only - FALSIFIED

Mirage A "Stairs" and dust2 B site: the box corner catches the top edge of a wall, the sweep reports a near-vertical edge-axis normal, the sim rests on the rim while the real grenade slid off.
Tried: a grenade may only come to rest on a triangle face (or with a face under the hull), never on an edge-axis contact.
Corpus: dust2 284 to 301, mirage 34 to 42, ancient 28 to 43, overpass 16 to 18; fixed nothing anywhere.
The game does rest grenades on rims. Whatever separates the rim cases is not "edge versus face".

### Loop iteration 3 (2026-09-04): the floor-damp gate sits above 689 - needs Nick's decision

The biggest ancient (15 throws) and overpass (10 throws) clusters are the same event: a first floor bounce at an incoming speed the captures put at 690 u/s, where the game left the bounce undamped and the sim, with `DampGateSpeed = 689`, damped it to half the speed.
Measured directly from 3,030 steep floor bounces above 600 u/s in the captures (ratio of outgoing to incoming speed, normalised by the damp factor): every bounce from 690.75 up is damped, 689.25 to 689.75 are all undamped, 690.0 to 690.5 are mixed (35 damped, 16 not).
The sample speed carries about a quarter tick of gravity uncertainty, so the true gate is somewhere in 689.5 to 690.75.

Candidate gates scored on the corpus (misses over 8u; baseline 689: dust2 284, mirage 34, ancient 28, overpass 16, inferno 9, anubis 6, nuke 3, office 0 = 380):

| gate | dust2 | mirage | ancient | overpass | total |
|------|------:|-------:|--------:|---------:|------:|
| 689.5 | 284 | 32 | 33 | 11 | 378 |
| 690.0 | 285 | 32 | 27 | 11 | 373 |
| 690.5 | 281 | 34 | 22 | 37 | 375 |

690.0 is the only candidate that passes the loop's rule (no map worse by more than 2) and it is the best total, but overpass swings from 11 to 37 between 690.0 and 690.5, so a lot of real throws sit right on the gate.
Not applied: the loop's rule 3 protects the damp gate as an engine constant. Decision for Nick.
Also from this iteration: `extract --dump --probe "x,y,z;..."` prints the world physics parts, the collision attribute table with interaction and exclusion layers, and the per-triangle surface materials around each probe point.

### Loop iteration 4 (2026-09-04): rest needs support under the centre - FALSIFIED

Narrower form of iteration 2: keep resting on edge contacts, but require a point of floor within 3u under the hull centre (a corner on a rim with the centre past the edge slides off).
Corpus: dust2 284 to 311 (4 fixed, 31 broken), mirage 34 to 45, ancient 28 to 41, overpass 16 to 21.
Real grenades park on rims with the centre overhanging. The mirage A stairs and dust2 B lip misses are not a rest-rule problem; the remaining suspect is geometry a unit off along those rims.

### Loop iteration 5 (2026-09-04): the July dust2 corpus is a different map

`diverge --summary` ranks the triangles the sim bounces off while the real grenade did not, across every miss of a map, with the game build each throw was recorded on.
On dust2 every one of the top eight phantom surfaces is hit only by throws from build 2000839 (the 9 to 10 July reports): a post above the B top window (39 misses), the CT-mid wall, the B site lip, the long boost box.
The same post takes 21 sim contacts from the 30 Aug and 3 Sep throws (builds 2000877 and 2000872) and every one of them is matched by a real bounce.
Valve changed dust2 between builds 2000839 and 2000872; the meshes are extracted from build 2000899.

Scoreboard by recorded build (over 8u): dust2 2000839 3,423 throws 6.7%; 2000872 1,135 throws 3.1%; 2000877 575 throws 3.3%.
No map has any throw recorded on build 2000899, the build the meshes come from, so mirage's remaining clusters (A stairs block, ladder platform: world hulls real grenades pass through) may be the same story between 2000872 and 2000899.
`replay --build <id>` scores one build's throws only.

Consequence for the accuracy loop: the 3,423 July dust2 throws cannot be matched by any physics change and should not count; the comparable corpus is what was recorded on 2000872 and later.
One fresh validation pass on the current build, per map, is what makes the scoreboard trustworthy again - it needs the rig (rule 5).

### Loop iteration 6 (2026-09-04): face normal on a near-tie edge contact - FALSIFIED again, on comparable builds

Re-scored the crate-rim idea (report the face contact when it lands within a window of the earliest edge-axis contact) on throws from builds 2000872 and later only, in case the first verdict was an artefact of the old-map corpus.
Comparable-build misses over 8u: window 0 (off) 150, 0.02 154, 0.1 166. Worse either way; the SAT sweep's edge-axis normals are what the game does at crate rims.

Loop state after six iterations: kept 1 (two contacts per tick), falsified 4 (rest on faces, centre support, face tie twice), measured 1 that needs a decision (damp gate).
On comparable builds the corpus is at 150 misses of 7,447 with every map at or above 90% within 3u except mirage (88.6%), whose remaining clusters are world hulls real grenades pass through and may be the same map-version story.
Both remaining moves need Nick: the gate constant, and a validation pass on the current build (2000899) across the maps.
Edge tipping re-scored on comparable builds only (constants override EdgeTipping=true): 198 misses over 8u versus 150 with it off (mirage 34 to 45, ancient 28 to 37, anubis 6 to 13). Off stands.

### Loop, later on 2026-09-04: gate set to 690, rig validation on the current build

Nick: "do whatever you recommend, don't stop for me". `DampGateSpeed` 689 -> 690.0 (measured band; corpus 380 -> 373, comparable builds 150 -> 143 expected). The rig runs `accuracy-run.sh` over every map with nav data on build 2000899 so the corpus finally matches the meshes.

### 2026-09-04, later: the rig server was seven weeks stale

Nick: "make sure we are updating the maps to the latest versions too".
The rig's dedicated server was on CS2 build 2000872 (16 July) while the client, the meshes, and Steam's public branch were on 2000899 (25 Aug).
Every validation report labelled its `build` from the mesh, not the server, so the reports since July claimed 2000877 and 2000899 for throws that happened on 2000872.
The dust2 "map change between 2000839 and 2000872" is therefore real (the server did update on 16 July), and everything after that is a 2000872 map compared against newer meshes.
Reports now record `serverBuild`, `validate` warns on a mismatch, the server is being updated, and the full validation pass restarts on the matching build.

### Loop iteration 7 (2026-09-04): breakable glass - KEPT

First same-build data (server and mesh both 2000899): cs_office target 3 had six misses, five of them the real grenade meeting a row of prop_dynamic office windows the mesh no longer had (prop_dynamic was dropped for the nuke vent).
Measured from the captures: every grenade through an intact pane kept its heading and left at exactly 0.40 of its speed; one through a pane an earlier throw had broken lost nothing.
Shipped: prop_dynamic merged as EntityBreakable when the model carries a `break_list` and no `break_command_list` (windows yes, nuke vent slats no); `ThrowConstants.GlassPassFactor = 0.40`; the exact sim passes through glass at that factor and ignores the broken pane for the rest of the flight.
Corpus: office 12 to 8 on same-build throws, nuke 3 to 3, dust2/mirage/ancient/overpass/inferno/anubis unchanged (their glass props were never hit). Tests: GlassPassThroughTests.
Not modelled: the voxel sweep still sees glass as solid, so a lineup whose only route is through a window is not proposed; and within one validation batch the first throw breaks a pane for the rest, which real rounds reset.

### Loop iteration 8 (2026-09-04): community maps' models live in their own addon VPK - KEPT

cs_shelter on matching builds was the weakest map (16 misses in 388, 86% within 3u), and the top cluster was four throws that met a row of garage windows the mesh did not have.
The extractor searched only the map VPK and csgo/pak01_dir.vpk; community maps (cs_shelter, de_boulder, de_fachwerk) keep every prop in game/csgo_community_addons/<map>/<map>_dir.vpk, so all of their window props, static props and entity models were "model not found" and skipped without a word.
The extractor now searches that VPK too, prints "NOT FOUND" for any model it still cannot resolve, and `rig/s2geo-dump.py` reads a mesh from the shell.
Corpus: cs_shelter 16 to 9 on same-build throws (2.3% over 8u, 87.9% within 3u); official maps unaffected. de_boulder and de_fachwerk re-extracted with the same fix.

### First full pass on matching builds (2026-09-04, 15:16 to 17:51)

Server and meshes both on CS2 build 2000899, 15 maps, 56 targets, 3,904 graded throws, no failures. Scored against the current meshes and physics (`replay --build 2000899`):

| map | throws | median | within 3u | over 8u |
|-----|-------:|-------:|----------:|--------:|
| cs_italy | 240 | 0.46 | 93.8% | 4 |
| cs_office | 239 | 0.69 | 89.1% | 8 |
| cs_shelter | 388 | 0.41 | 87.9% | 9 |
| de_ancient | 240 | 0.67 | 93.8% | 3 |
| de_anubis | 230 | 0.51 | 93.5% | 2 |
| de_boulder | 239 | 0.62 | 90.4% | 3 |
| de_cache | 240 | 0.39 | 95.4% | 1 |
| de_dust2 | 422 | 0.40 | 95.5% | 5 |
| de_fachwerk | 240 | 0.41 | 95.4% | 4 |
| de_inferno | 232 | 0.47 | 93.1% | 2 |
| de_mirage | 240 | 0.39 | 95.8% | 1 |
| de_nuke | 240 | 0.40 | 95.4% | 1 |
| de_overpass | 234 | 0.72 | 89.7% | 6 |
| de_train | 240 | 0.41 | 90.8% | 3 |
| de_vertigo | 240 | 0.46 | 92.5% | 2 |
| total | 3,904 | | 92.8% | 54 (1.4%) |

The loop's first target (fewer than 200 over 8u) is met on this corpus; office, shelter and overpass sit under the 90% within-3u bar.
Seven of these maps had never been validated before today.

### host_timescale 3 on the rig (2026-09-04): physics identical, kept

Nick asked whether the game could simply run faster. The plugin captures every tick, so game speed should not touch the physics; measured on dust2's eight markers at `host_timescale 3` against the normal-speed run of the same afternoon: median, p90, within-3u and over-8u identical on every marker (423 vs 422 throws, 404 vs 403 within 3u, 5 vs 5 over 8u).
Wall time for the eight targets: 14.1 min at 3x versus 16.9 at 1x, the modest gain because the solve, not the throw phase, dominates a target now that solves are prefetched, and one lost capture cost the old 120 s idle wait (now 30 s).
`batchvalidate --timescale N` sends the cvar after every level change (the plugin's command allowlist now includes it; a hot reload did not pick up the new allowlist, a server restart did).

### Loop iteration 9 (2026-09-04): a narrower grenade hull - FALSIFIED

Hypothesis from the dust2 top_door, B-site lip and mirage rim misses: the real grenade leaves beam edges where our 2u box hull comes to rest, so the game's effective hull might be narrower than `GRENADE_DEFAULT_SIZE`.
Tried: `ThrowConstants.HullHalfExtent` swept over 2.0 / 1.5 / 1.0 on all 15 maps, same-build corpus (`--build 2000899`).
Over 8u totals: 2.0 = 59 (baseline at the time), 1.5 = 227, 1.0 = 457; dust2 within 3u fell from 95.5% to 69.0% at 1.0.
Every map got worse at every narrower size, so the box half-extent is 2u and the rim misses are not a hull-size effect.
The knob was reverted; do not retry.

### Loop iteration 10 (2026-09-04): slow wall contacts bounce, they do not slide - KEPT

dust2 top_door [17] (214u): sim and capture agree tick for tick until a 41 u/s contact with the door frame; the real grenade leaves at the reflected velocity (-4,17,-8) and later rolls over a ledge, the sim slid along the frame with the sideways component removed and rested on the ledge.
Cause: the rest branch (`|vAfter| < StopSpeed`) handled the no-floor case by sliding along the wall with the incoming velocity's tangential part, which drops exactly the component the wall reversed.
Fix: a slow contact with no floor under the hull is an ordinary bounce; the rest check stays as it was.
Corpus (same build, 15 maps): 59 -> 56 over 8u; within 3u up on italy, office, anubis, dust2 (95.5 -> 96.0) and vertigo, no map down.
Test: `SlowWallContactTests`.
Still open on the same target: [11] and [16] rest balanced on the top edge of a wall the real grenade rolls off (the rim case that edge tipping, faces-only and centre-support all failed on).

### Loop iteration 11 (2026-09-04): a narrower hull in x/y only - FALSIFIED

Third and last hull-shape attempt (after the sphere and the uniformly smaller box).
Hypothesis: the rim balances need a hull whose corner reaches less far past an edge, so narrow x/y and keep the 2u height so floor contacts are unchanged.
`HullHalfWidth` 1.75 / 1.5 / 1.0 on the same-build corpus, 15 maps: 111 / 201 / 425 over 8u against 56; every map worse already at 1.75 (dust2 96.0% -> 92.7%, ancient 93.8% -> 89.2%).
The box is 2u in every axis and the hull shape is not where the rim misses come from; do not try hull-shape changes again.

### Loop iteration 12 (2026-09-04): rest only on gentle slopes - FALSIFIED

Hypothesis from train [48] (sim parks on a 34 degree slope the real grenade slides down) and anubis [54]: a slow contact should only count as rest when the surface normal is steeper than FloorNormalZ (0.7).
`RestNormalZ` 0.8 / 0.9 / 0.95 on the same-build corpus, 15 maps: 56 / 56 / 65 over 8u against 56; within 3u unchanged at 0.8, dust2 96.0 -> 95.7 at 0.9, dust2 94.8 and overpass 88.0 at 0.95.
The slope cases do not respond (the grenade micro-hops and rests anyway) and steep thresholds break genuine rests on ramps. Reverted; do not retry a rest-normal threshold.

### Loop iteration 13 (2026-09-04): roll-out after the rest condition - KEPT (rule 2 judgement call)

The 3-8u band on office, shelter and overpass is 71 of 79 throws "settle": paths agree, only the rest differs by 3-5u.
`replay --rollout` measured where the real rest lies relative to the sim's instant stop, in the frame of the last tangential velocity: along +0.2u at 0-4 u/s, +0.6-1.3u at 4-8, +0.6-1.2u at 8-12, +0.7-1.9u at 12-16, +2-3u at 16-20; across 0.1-0.3u.
That is 0.1u per u/s: the grenade keeps sliding for about a tenth of a second before the engine sleeps it.
Model: on the rest condition, slide along the floor at the post-bounce tangential velocity for `RolloutTime` = 0.1 s (six ticks); walls end the roll; losing floor under the hull centre means a rim, and the roll ends there.
Sweep 0.05 / 0.1 / 0.15 s: 0.1 best overall; 0.15 helps office/boulder but costs italy, ancient, mirage and dust2.
First version let the roll carry the hull corner over rims (dust2 [51] on a sloped ledge fell 57u; mirage [15] fell 7u); the centre-support stop fixed both.
Result (same build, 15 maps): within 3u up on 12 maps (overpass 89.7 -> 93.2, office 89.5 -> 91.2, boulder 90.4 -> 94.1, anubis 93.9 -> 96.5, train 90.8 -> 93.8), down on vertigo (92.9 -> 92.5) and mirage/dust2 flat; medians down on 13 maps; over 8u 56 -> 56 (boulder -1, train -1, dust2 +2: [8] and [33] flip from 7.x to 8.x, both real grenades rolled 7-8u, further than the model).
Rule 2 asks for the over-8u total to drop; it is unchanged, and the change moves the within-3u half of the target on most maps, so it is kept under Nick's "do what you recommend" - revert with `RolloutTime = 0` if he disagrees.
Under 90% now: shelter only (88.7).

### Loop iterations 14-16 (2026-09-04): the bounce tick - three models FALSIFIED

Observation (cs_shelter 144929 [40]/[44], flat-floor crouch throws, 35 throws at 4-5u): sim and real post-bounce velocities are identical at every hop, yet each real hop lasts about one tick longer and lands 1-2u further along; the real impact speed is 3 u/s larger than the launch speed of the same hop.
14. Spend only a fraction of the bounce tick's remainder at the reflected velocity (`BounceRemainder` 0.0 / 0.5): over 8u 58 / 62 against 56; shelter within 3u 88.7 -> 92.0 / 94.8, vertigo -> 97.5 / 98.8, but dust2 over 8u 10 -> 12 / 16 and overpass 93.2 -> 91.0 / 91.9. Right direction on flat floors, wrong somewhere else; too crude as a rule.
15. Integrate position with the velocity from before the tick's gravity update instead of the trapezoid (`GravityLead` 0): within 3u collapses to 24-36% on the first three maps; free flight is integrated correctly and the effect is confined to the bounce tick.
16. Resolve the contact at the end of the tick (full tick at the pre-bounce velocity, push out along the normal, reflected velocity from the next tick): 354 over 8u, within 3u 61-83% per map. The sub-tick contact model is right.
Next: measure instead of guessing - for every paired sim/real bounce, the position offset the bounce tick itself introduces, as a function of the sub-tick fraction and the velocities, then fit the model to that.

### Loop iteration 17 (2026-09-04): bounce-tick offsets measured, remainder gravity - FALSIFIED

`diverge --offsets` (new): for every paired sim/real bounce, the position offset the bounce tick introduces (real - sim two ticks after, minus the offset one tick before), in the frame of the pre-bounce horizontal velocity, with the sub-tick fraction T and both post-bounce velocities.
7,263 paired bounces on six maps, 5,079 matched floor bounces: the bounce tick introduces no offset (along median 0.00u, mean 0.1u, no dependence on T or speed; side 0.00u).
Post-bounce velocities: horizontal identical (median difference 0.00 u/s); vertical fits real = sim reflection - 3.33 (1 - T) u/s where the sim subtracts 5 (1 - T) for the remainder's gravity.
Implemented as `RemainderGravity` 0.667 / 0.0: 63 / 312 over 8u against 56, within 3u down on 11 maps at 0.667. The fit does not transfer, so the (1 - T) dependence is an artifact of how the capture samples velocity within a tick, not an engine rule. Reverted.
Where the flat-floor hop drift comes from is still open; the bounce tick itself is exonerated.

### Loop iteration 18 (2026-09-04): two physics steps per tick - KEPT

`diverge --ticks a-b` (new) dumps sim and real state per tick. On the shelter flat-floor hops the drift was entirely vertical velocity: the game left the bounce with exactly 0.45 x the impact velocity and no remainder gravity, while the sim took 5 u/s x (1 - T) off.
On italy's steep impacts the game did take about 3.6 u/s off. Binning 3,753 paired floor bounces by the sub-tick fraction T: T < 0.5 -> 3.6 u/s taken off (median), T >= 0.5 -> 0.0. No dependence on speed or angle.
That is a 128 Hz physics step inside the 64-tick server: gravity per half-step, contact resolved within its half-step from the half-step velocity, no further gravity in that half-step. Free flight integrates identically.
Result (same build, 15 maps): 56 -> 38 over 8u; within 3u: italy 97.9, office 95.4, shelter 94.8, ancient 98.8, anubis 100, boulder 98.3, cache 98.8, dust2 98.6, fachwerk 99.6, inferno 98.7, mirage 99.2, nuke 99.6, overpass 98.7, train 98.3, vertigo 99.2. Both halves of the loop target are met.
Trap found on the way: .NET 10 on-stack replacement mis-compiled the new nested loop once hot (the same throw fell through the floor only after sixty others had run in the process; `DOTNET_TC_OnStackReplacement=0` fixed it). `SimulateExactRaw` is now `[MethodImpl(AggressiveOptimization)]`, which keeps it out of tiering. Three `VerifyExactTests` that failed for the same reason pass again.
Follow-up: the roll-out (iteration 13) was fitted against the old, shorter hops; re-measure `RolloutTime` under this model.

### Loop iteration 19 (2026-09-04): the roll-out was compensating for the bounce-tick error - REMOVED

Re-sweep of `RolloutTime` under the two-step tick: 0.1 / 0.05 / 0 s all give 38 over 8u; within 3u equal or better at 0 on every map but anubis (100 -> 99.6, one throw); medians 0.38 -> 0.03u on every map at 0.
The 0.1u per u/s "roll" measured in iteration 13 was the hop-length shortfall of the single-step bounce, not a rolling phase. The roll-out code, its constant and its tests are gone; `replay --rollout` stays as the measurement.
Same-build corpus now: 38 over 8u of 3,904, medians 0.03-0.04u, within 3u 95.6% (shelter) to 99.6%.

### Loop iteration 20 (2026-09-04): the damp gate is judged on the full-tick velocity - KEPT

Regression check after the two-step tick: 21 of the 38 misses were hits when graded (`replay --worst` shows "(was Xu)"; the classifier lists misses by recorded error and hides them). dust2 [55] hit the floor at 692 u/s full-tick, 689.8 at the half-step, and stopped damping.
Re-measured on 590 steep floor bounces between 600 and 800 u/s from the captures: the full-tick speed separates damped from undamped at 688-690 with no exceptions; the half-step speed misclassifies 2 at 690 and 4-5 at 690.5-691.
`Bounce` takes the gate speed separately; the reflection still uses the half-step velocity. 38 -> 33 over 8u (dust2 10 -> 8, cache 2 -> 0, ancient 2 -> 1), no map worse; medians 0.03u everywhere.
Remaining regressions on shelter and office are glass state: panes broken by an earlier throw in the same validation run let later throws through at full speed, the sim treats every pane as intact. All-glass-as-air is worse (shelter 7 -> 16, office 7 -> 11), so the state is mixed within a run. Needs a rig or grading decision.

### Loop iteration 21 (2026-09-04): glass met as the second contact of a step - NO EFFECT, reverted

The sub-step loop's second-contact branch bounces off a breakable pane instead of passing through it (office [49] showed the sim bouncing off EntityBreakable#10 in the same tick as a floor contact).
Made it pass through like a first contact: 33 -> 33 over 8u, every map identical to three decimals; no throw in the corpus reaches a pane as the second contact of a step with the pane still intact. Reverted under rule 2; if a case ever appears, this is the fix.

### Loop iteration 22 (2026-09-04): one contact per half-step - FALSIFIED

dust2 [35] hits a 17u door-frame sliver and then the floor inside one half-step and is reflected twice (the game bounces once), so `BouncesPerTick` 1 was tried under the two-step tick: 34 over 8u against 33 (boulder 0 -> 1), dust2 unchanged. The second contact per step stays.
Remaining 33 misses by kind: about 8 are glass state (panes broken by an earlier throw in the same run: shelter 152646 [19-21], 152857 [32], 144929 [59]; office 144326 [49], [57], 143837 [44]), dust2 [39]/[50]/[35]/[42] x2 are geometry-fragile corners and ledges that flipped when the trajectories moved by fractions of a unit, the rest are single throws.

## Glass loop (2026-09-05)

### Iteration 1: ground truth for every breakable - `probe`

Corpus scan (`diverge --breakables`): 30 breakable contacts, all cs_office/cs_shelter, 23 intact panes at exactly 0.40, 7 panes already gone (1.00) after an earlier throw in the same run. Nothing on any other map had ever touched a breakable.
`probe` fires one synthetic grenade through every breakable/door cluster on the rig (see physics-sim.md for the results). Nuke needs no glass logic (everything bounces, sim agrees); inferno/ancient/anubis/office/shelter panes break and pass at 0.40; train's electrical box is misclassified (sim passes, game bounces).
Probe launch points in the void (deck windows on inferno, vertigo railings) produce 6,000-9,000u "errors" that are the void, not physics.

### Iteration 2: glass state in the solver and the grader - KEPT

Lineups carry `GlassBreaks` / `RestIfBroken` (VerifyExact re-simulates glass-breaking throws against a glass-gone collider); the API exposes `glass`, `restIfBroken`, `stateDependent` (landings differ by more than 8u) and ranks state-dependent lineups below concealed ones; the viewer badges them amber with the alternative landing distance.
Validation records `GlassState` per throw from the capture (speed ratio at the sim's pane tick) and grades against the matching rest; `replay` does the same from recorded state, or the closer of the two for old reports and says how many it corrected.
Corpus: 33 -> 26 over 8u (office 7 -> 5, shelter 7 -> 2), 7 throws re-graded against a gone pane, medians unchanged at 0.03u.

### Glass loop iteration 3 (2026-09-05): only window and glass props let a grenade through - KEPT

Breakable model prop data cannot be read from the compiled model (the KV3 block carries the keys, the values are empty), so the rule rests on the probe: every pane the game let a grenade through has "window" or "glass" in its model path (office, shelter, anubis, nuke and train windows, inferno shop-front glass and apartment windows, ancient lantern glass, overpass door glass); the one breakable prop it bounced off, de_train's electronics enclosure doors, has neither.
Extraction now keeps such props as EntitySolid. Re-extracted de_train (EntityBreakable 432 -> 72 triangles, the rest solid) and de_vertigo (its fence rails become solid; the game bounced there too). Corpus unchanged on both (1 and 1 over 8u, medians 0.03u); the dry-run probe shows the sim bouncing at the enclosure like the game.

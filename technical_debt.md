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

Themes: the physics core is strong but wrapped in fragile I/O boundaries; the three load-bearing rig mechanisms (signature scan, meta queue, file IPC) all fail silently; nothing long-lived is supervised; the viewer's 3D lifecycle leaks; accessibility is the weakest UI axis; the exact-physics stack that decides every answer has zero test coverage.

## Progress

- **Batch 1 - completed 2026-07-10.**
  Fixed: C1, H24, L24 (gitignore + baseline commit + scratch cleanup), H12, H17, M17, M35 (atomic rename IPC end to end, shared calibipc.py), H20, H21, H22, M18 (watcher claim-by-rename, stale discard, solver-error distinction, stderr to rig.log, malformed-request quarantine), H13 (offset tailing with rotation guard), H23 (50 MB capture rotation), H4 (async capture writer off the tick thread), H5 (AddTimer everywhere, slot-safe callbacks), M16 (Unload flushes captures and beams), M22 (marker/array validation), M34 (summary from JSON report), plus a watcher heartbeat (C2 groundwork).
  Verified: build + 18/18 tests, live end-to-end throw through the new protocol (capture persisted by the writer thread, legacy 317 MB file auto-rotated), chat relay, heartbeat.

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

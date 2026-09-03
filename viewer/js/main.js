// Boot and cross-module orchestration. This is the only module allowed to
// import the feature modules; they call back into the orchestrators defined
// here (setTarget, select, runQuery) via the init*/set*Callbacks hooks.

import { state, filtered, esc, lowMemoryDevice, loadFavorites, setFavorite, isFavorite, DEFAULT_EYE_HEIGHT, EYE_HEIGHT_BY_TYPE, TARGET_SNAP_RADIUS, favoriteHooks, loadSavedLocal, persistSavedLocal} from "./state.js?v=103";
import { loadMapList, loadMapData, runQuery as postLineupQuery, fetchTrajectory, fetchLineupOne, fetchSlack, fetchSpawns, fetchProSmokes, fetchMeshDiff, meshDiffExists, fetchLevels, fetchSmokeCoverage, runExecute, findExecuteSpots, fetchTargets, fetchMe, signOut, fetchSavedLineups, putSavedLineups, fetchVotes, castVote} from "./api.js?v=103";
import { loadRadar, readColors, recolorRadar, draw, scheduleDraw, resize, resetView, initMap2d, screenOf } from "./map2d.js?v=103";
import { ensure3d, resetEnsure3d, teardown3d, current3d, sync3d, syncProgress3d, syncMeshDiff3d, set3dCallbacks, applyTheme3d, verticalFovFromDesired } from "./view3d.js?v=103";
import { resetEnsureTexturedScene } from "./textured-scene.js?v=103";
import { capturePreview } from "./preview.js?v=103";
// Every local import across viewer/js carries the SAME ?v= token, bumped
// together on any change. The HTML is served no-cache, so a fresh load pulls
// main.js?v=N, which pulls every module at ?v=N - the whole graph refreshes as
// one consistent set past Cloudflare's 4h JS cache, with no duplicate module
// instances (which a partial versioning would cause). Bump the token everywhere.
import { renderLineups, initPanel, revealSelected, resultStatusText } from "./panel.js?v=103";

(async () => {
  // Map switching means a failed load is no longer necessarily terminal (the
  // user can just pick a different map), so a stale error from a previous
  // attempt must not linger once a later one succeeds.
  function clearBootError() {
    document.getElementById("boot-error")?.remove();
  }
  function bootError(file) {
    clearBootError();
    const box = document.createElement("div");
    box.id = "boot-error";
    box.style.cssText = "position:fixed; inset:0; z-index:60; display:flex; align-items:center; justify-content:center";
    box.innerHTML =
      `<div style="background:var(--panel); border:1px solid var(--line); border-radius:8px; padding:18px 24px; max-width:460px">` +
      `<b>failed to load ${esc(file)}</b><br>` +
      `<span style="color:var(--muted)">regenerate it with the <code>viewerdata</code> CLI command, then reload</span></div>`;
    document.body.appendChild(box);
  }

  // Which pointer the person is actually using, so the 3D help shows WASD and
  // right-drag on a desktop and gestures on a phone. Starts from what the
  // browser reports, then trusts the first real event over that: a hybrid
  // laptop reports touch capability while being driven by a mouse, and some
  // automation browsers report no hover at all.
  function setPointerKind(coarse) {
    document.documentElement.classList.toggle("pointer-coarse", coarse);
    document.documentElement.classList.toggle("pointer-fine", !coarse);
  }
  setPointerKind(matchMedia("(pointer: coarse)").matches && navigator.maxTouchPoints > 0);
  addEventListener("touchstart", () => setPointerKind(true), { once: true, passive: true });
  addEventListener("mousemove", e => {
    // A touch also emits a synthetic mousemove; a real mouse moves without one.
    if (e.sourceCapabilities?.firesTouchEvents !== true) {
      setPointerKind(false);
    }
  }, { once: true });

  const canvas = state.canvas;
  const stage3d = state.stage3d;
  const statusEl = state.statusEl;
  const pickBtn = document.getElementById("pick");
  const searchSeg = document.getElementById("search-seg");
  const searchLabel = document.getElementById("search-label");
  const searchBtnFor = where => searchSeg.querySelector(`[data-search="${where}"]`);
  const heatBtn = document.getElementById("heat");
  const viewSeg = document.getElementById("view-seg");
  const overlaySeg = document.getElementById("overlay-seg");
  const viewBtn = mode => viewSeg.querySelector(`[data-view="${mode}"]`);
  const overlayBtn = name => overlaySeg.querySelector(`[data-overlay="${name}"]`);
  const pickOriginBtn = document.getElementById("pick-origin");
  const targetIn = document.getElementById("target-in");
  const originIn = document.getElementById("origin-in");
  const resetViewBtn = document.getElementById("reset-view");
  const copyTargetBtn = document.getElementById("copy-target");
  const copyOriginBtn = document.getElementById("copy-origin");
  const spawnsBtn = overlayBtn("spawns");
  const targetsBtn = overlayBtn("targets");
  const proSmokesBtn = document.getElementById("prosmokes");
  const proSideSeg = document.getElementById("prosmokes-side");
  const topDownBtn = overlayBtn("topdown");
  const reticleBtns = [...document.querySelectorAll("#reticle-seg .tile")];
  const collisionBtn = overlayBtn("collision");
  const meshdiffBtn = overlayBtn("meshdiff");
  const coverageBtn = overlayBtn("coverage");
  // setpos places the player's ORIGIN, and the engine keeps the origin a hair
  // above the floor (Valve's 0.03125). Handing out the floor height itself puts
  // that hair's width of the player inside the world; adding the gap back is
  // the difference between "the floor" and "where a player standing on it is".
  // Declared up here: a permalink applies during boot, before the setpos code
  // further down has run, and a `const` read before its line is a
  // ReferenceError - which silently failed every ?t= link's first render.
  const FEET_ABOVE_FLOOR = 0.03125;
  const coord = v => Number.parseFloat(v.toFixed(2)).toString();
  // What /api/maps said about each map; filled in at boot, read by
  // syncControls, so it lives up here rather than in the temporal dead zone
  // of a `let` further down.
  let mapList = [];
  const cardExecute = document.getElementById("card-execute");
  const executeCount = document.getElementById("execute-count");
  const executeClear = document.getElementById("execute-clear");
  const executeHint = document.getElementById("execute-hint");
  const executeList = document.getElementById("execute-list");
  const executeAdd = document.getElementById("execute-add");
  const executeSolveLabel = document.getElementById("execute-solve-label");
  const executeSeg = document.getElementById("execute-seg");
  const executeSpotsLabel = document.getElementById("execute-spots-label");
  const executeSpots = document.getElementById("execute-spots");
  const openSavedBtn = document.getElementById("open-saved");
  const savedCountEl = document.getElementById("saved-count");
  const panelModeEl = document.getElementById("panel-mode");
  const rulerEl = document.getElementById("lineup-ruler");
  const clearBtn = document.getElementById("clear");
  const panelEl = document.getElementById("panel");
  const panelCollapseBtn = document.getElementById("panel-collapse");
  const keyEl = document.getElementById("key-dots");
  const mapSelect = document.getElementById("map-select");
  const cancelBtn = document.getElementById("solve-cancel");
  let solveController = null;

  cancelBtn.addEventListener("click", () => solveController?.abort());
  // Dev handle: lets a browser console (or an automated check) fly the camera
  // to a coordinate to inspect rendering at a specific spot.
  window.__view3d = current3d;
  window.__state = state;
  window.__map2d = { screenOf };

  // One control, three exclusive aiming overlays: none, the centre crosshair,
  // or the full-screen lineup ruler. They were two independent toggles, which
  // let both draw at once - two crosshairs over each other, neither of them
  // the one being aimed with. The choice is a preference, so it persists.
  const RETICLE_MODES = ["off", "cross", "smoke"];
  const savedMode = localStorage.getItem("smokesolver.reticleMode");
  state.reticleMode = RETICLE_MODES.includes(savedMode)
    ? savedMode
    // Migrates the old two-toggle preference: a hidden crosshair stays hidden.
    : (localStorage.getItem("smokesolver.crosshair3d") === "0" ? "off" : "cross");
  applyReticleMode();
  for (const b of reticleBtns) {
    b.addEventListener("click", () => {
      state.reticleMode = b.dataset.reticle;
      localStorage.setItem("smokesolver.reticleMode", state.reticleMode);
      applyReticleMode();
      buildRuler(rulerEl);
      syncControls();
      current3d()?.focusStage();
    });
  }
  // The two booleans the drawing code already reads, derived from the mode so
  // "exactly one overlay" is guaranteed by construction rather than by two
  // handlers agreeing with each other.
  function applyReticleMode() {
    state.crosshairOn = state.reticleMode === "cross";
    state.reticleOn = state.reticleMode === "smoke";
  }
  collisionBtn.addEventListener("click", () => {
    state.collisionOn = !state.collisionOn;
    current3d()?.setCollisionOverlay(state.collisionOn);
    syncControls();
    current3d()?.focusStage();
  });
  // Coverage is asked about while PLACING the target, so it reloads whenever the
  // target moves rather than only when the button is pressed - a stale bloom
  // sitting under a target you have since moved is worse than none.
  async function loadCoverage() {
    if (!state.coverageOn || !state.target) {
      state.coverage = null;
      return;
    }
    const gen = state.mapGeneration;
    const at = state.target;
    try {
      const data = await fetchSmokeCoverage(state.currentMap, at);
      // The map may have changed, or the target moved on, while this was in
      // flight; either way this answer is about a question nobody is asking.
      if (state.mapGeneration !== gen || state.target !== at) {
        return;
      }
      state.coverage = data;
    } catch (err) {
      state.coverage = null;
      statusEl.textContent = `coverage unavailable: ${err.message}`;
    }
    syncControls();
    scheduleDraw();
    sync3d();
  }

  coverageBtn.addEventListener("click", async () => {
    state.coverageOn = !state.coverageOn;
    state.coverage = null;
    syncControls();
    scheduleDraw();
    sync3d();
    if (state.coverageOn && !state.target) {
      statusEl.textContent = "pick a target first - coverage shows what a smoke landing there would fill";
      return;
    }
    await loadCoverage();
  });

  // ---- the account: sign-in, and favourites that follow it ----

  const signinEl = document.getElementById("signin");
  const whoamiEl = document.getElementById("whoami");
  const signoutBtn = document.getElementById("signout");

  function syncAccountUi() {
    const me = state.account;
    signinEl.hidden = !!me;
    whoamiEl.hidden = !me;
    signoutBtn.hidden = !me;
    if (me) {
      const avatar = document.getElementById("avatar");
      const persona = document.getElementById("persona");
      persona.textContent = me.name ?? `Steam \u2026${me.steamId.slice(-5)}`;
      avatar.hidden = !me.avatar;
      if (me.avatar) { avatar.src = me.avatar; }
      whoamiEl.title = `Signed in as ${me.name ?? me.steamId} (SteamID ${me.steamId}). Saved lineups and votes are kept on this account.`;
    }
  }

  // Push the whole set after a change, coalesced so a burst of stars is one
  // write. Replace-whole is deliberate: the client always holds the full set
  // and the server stores it atomically, so there is no partial state to get
  // wrong.
  let saveTimer = null;
  function scheduleAccountSave() {
    if (!state.account) {
      return;
    }
    clearTimeout(saveTimer);
    saveTimer = setTimeout(async () => {
      try {
        await putSavedLineups(state.saved);
      } catch (err) {
        statusEl.textContent = `could not save to your account: ${err.message}`;
      }
    }, 600);
  }
  favoriteHooks.onChange = scheduleAccountSave;

  async function loadAccount() {
    const me = await fetchMe().catch(() => null);
    state.account = me;
    syncAccountUi();
    if (!me) {
      return;
    }
    try {
      const { lineups } = await fetchSavedLineups();
      // Merge, never replace: what is on the account and what this browser
      // starred before signing in are both real. A lineup saved anywhere is
      // saved everywhere, keyed by its durable id.
      const byKey = new Map(lineups.map(l => [`${l.map}|${l.id}`, l]));
      for (const l of state.saved) {
        byKey.set(`${l.map}|${l.id}`, l);
      }
      state.saved = [...byKey.values()];
      persistSavedLocal();
      // And the star state for the current map reflects the merged set.
      for (const l of state.saved) {
        if (l.map === state.currentMap) {
          state.favorites.add(l.id);
        }
      }
      if (state.result?.lineups) {
        for (const l of state.result.lineups) { l._favorite = isFavorite(l); }
      }
      renderLineups();
      syncControls();
      scheduleAccountSave();
    } catch (err) {
      statusEl.textContent = `signed in, but your saved lineups did not load: ${err.message}`;
    }
  }

  signoutBtn.addEventListener("click", async () => {
    await signOut().catch(() => {});
    state.account = null;
    syncAccountUi();
    statusEl.textContent = "signed out - favourites stay in this browser";
  });

  // The redirect back from Steam lands here with ?signin=ok|failed; say so
  // once and drop it from the URL so a reload does not repeat it.
  {
    const params = new URLSearchParams(location.search);
    const signin = params.get("signin");
    if (signin) {
      statusEl.textContent = signin === "ok" ? "signed in with Steam" : "Steam sign-in did not complete - try again";
      params.delete("signin");
      history.replaceState(null, "", location.pathname + (params.toString() ? "?" + params : "") + location.hash);
    }
  }
  // What this browser saved comes first, so the account merge below has it to
  // merge into, and so Saved works at all for someone who never signs in.
  loadSavedLocal();
  loadAccount();

  // The live state, reachable from devtools. Nothing here is secret - it is
  // what the page is already showing - and being able to inspect it is how
  // "the chip is not rendering" gets diagnosed in a minute instead of an hour.
  window.smokeState = state;

  // ---- votes ----

  // The community's tally for the spot this result is about. Loaded after every
  // result so the arrows and the "most voted" sort have something to show; a
  // failure here is cosmetic and must never take the result down with it.
  async function loadVotes() {
    const target = state.result?.target;
    if (!target || state.result.execute) {
      state.votes = null;
      return;
    }
    const gen = state.mapGeneration;
    const forResult = state.result;
    try {
      const v = await fetchVotes(state.currentMap, target);
      if (state.mapGeneration !== gen || state.result !== forResult) { return; }
      state.votes = v;
      renderLineups();
    } catch (err) {
      console.warn("votes unavailable", err);
    }
  }

  async function voteOn(l, vote) {
    if (!state.account) {
      statusEl.textContent = "sign in with Steam to vote on lineups";
      return;
    }
    if (!l.id || !state.result?.target) {
      return;
    }
    const { error, data } = await castVote(state.currentMap, state.result.target, l.id, vote);
    if (error) {
      statusEl.textContent = error;
      return;
    }
    state.votes = data;
    renderLineups();
  }

  // ---- executes: several smokes from one stance ----

  // What a smoke in the list is called: the named target it sits on when
  // there is one, otherwise its coordinates. The number leads because that
  // is how the results and the map markers refer back to it.
  function executeTargetLabel(t) {
    const named = nearestNamedTarget(t);
    return named ? named.name : `${t[0].toFixed(0)}, ${t[1].toFixed(0)}`;
  }

  function renderExecuteList() {
    executeList.innerHTML = "";
    state.executeTargets.forEach((t, i) => {
      const li = document.createElement("li");
      li.className = "exec-row";
      const label = document.createElement("span");
      label.className = "exec-name";
      label.textContent = executeTargetLabel(t);
      label.title = `${t[0].toFixed(0)}, ${t[1].toFixed(0)}`;
      const drop = document.createElement("button");
      drop.className = "exec-drop";
      drop.type = "button";
      drop.textContent = "\u00d7";
      drop.title = "Remove this smoke from the execute";
      drop.setAttribute("aria-label", `Remove smoke ${i + 1}`);
      drop.addEventListener("click", () => {
        state.executeTargets.splice(i, 1);
        state.executeSpots = null;
        syncControls();
        scheduleDraw();
        sync3d();
      });
      li.append(label, drop);
      executeList.append(li);
    });
  }

  function renderExecuteSpots() {
    executeSpots.innerHTML = "";
    const found = state.executeSpots?.spots ?? [];
    executeSpots.hidden = found.length === 0;
    executeSpotsLabel.hidden = found.length === 0;
    // The label says what "worst" means, because a spot is only as good as its
    // hardest smoke and ranking on the average would hide exactly that.
    // What the HARDEST smoke of the set has to aim against - the thing that
    // decides whether the whole execute is reproducible. Same vocabulary the
    // rows use, so a spot and a lineup describe aim the same way.
    const worstWords = ["pinpoint", "close", "near", "reticle", "rough", "flat", "blind"];
    found.forEach((spot, i) => {
      const li = document.createElement("li");
      const btn = document.createElement("button");
      btn.type = "button";
      btn.className = "exec-spot" + (i === 0 ? " best" : "");
      const where = document.createElement("span");
      where.textContent = `${i === 0 ? "best" : `#${i + 1}`} \u00b7 ${spot.feet[0].toFixed(0)}, ${spot.feet[1].toFixed(0)}`;
      const note = document.createElement("em");
      note.textContent = worstWords[Math.min(spot.worst, 6)] ?? "";
      btn.append(where, note);
      btn.title = `Solve all ${state.executeTargets.length} smokes from here. The hardest of them lines up against: ${worstWords[Math.min(spot.worst, 6)] ?? ""}`;
      btn.addEventListener("click", () => solveExecuteFrom([spot.feet[0], spot.feet[1]]));
      li.append(btn);
      executeSpots.append(li);
    });
  }

  async function solveExecuteFrom(origin) {
    if (state.busy) {
      return;
    }
    const gen = state.mapGeneration;
    state.busy = true;
    syncControls();
    statusEl.textContent = `solving ${state.executeTargets.length} smokes from ${origin[0].toFixed(0)}, ${origin[1].toFixed(0)}\u2026`;
    try {
      const { error, data } = await runExecute(state.currentMap, origin, state.executeTargets);
      if (state.mapGeneration !== gen) { return; }
      if (error) { statusEl.textContent = error; return; }
      state.lastOrigin = origin;
      adoptExecute(data);
    } finally {
      state.busy = false;
      syncControls();
    }
  }

  // An execute answers as one result whose lineups carry which smoke they
  // belong to, so every existing thing - the markers, the selection, the
  // trajectory, the badges - keeps working without knowing about executes.
  function adoptExecute(data) {
    const lineups = [];
    data.smokes.forEach((smoke, i) => {
      smoke.lineups.forEach(l => {
        l._smoke = i;
        l._smokeTarget = smoke.target;
        lineups.push(l);
      });
    });
    lineups.forEach((l, i) => { l._idx = i; l._favorite = isFavorite(l); });
    state.result = {
      target: data.smokes[0]?.target ?? state.target,
      origins: 0,
      coverage: [],
      lineups,
      execute: { origin: data.origin, smokes: data.smokes.map(s => ({ target: s.target, found: s.found, emptyReason: s.emptyReason })) },
    };
    state.selected = lineups.length ? 0 : -1;
    const missing = data.smokes.filter(s => s.found === 0).length;
    statusEl.textContent = missing === 0
      ? `execute solved - ${data.smokes.length} smokes from one spot`
      : `${data.smokes.length - missing} of ${data.smokes.length} smokes work from that spot`;
    renderLineups();
    scheduleDraw();
    sync3d();
  }

  // Five players, five smokes: the server refuses more, and the button says
  // so before the click rather than after.
  const MAX_EXECUTE_SMOKES = 5;

  executeAdd.addEventListener("click", () => {
    if (!state.target || state.executeTargets.length >= MAX_EXECUTE_SMOKES) {
      return;
    }
    state.executeTargets.push([...state.target]);
    state.executeSpots = null;
    cardExecute.open = true;
    statusEl.textContent = `${state.executeTargets.length} smoke${state.executeTargets.length === 1 ? "" : "s"} in this execute - pick the next target, or solve it`;
    syncControls();
    scheduleDraw();
    sync3d();
  });

  executeClear.addEventListener("click", e => {
    e.preventDefault();
    e.stopPropagation();
    if (state.executeTargets.length === 0) {
      return;
    }
    state.executeTargets = [];
    state.executeSpots = null;
    statusEl.textContent = "execute cleared";
    syncControls();
    scheduleDraw();
    sync3d();
  });

  executeSeg.addEventListener("click", async e => {
    const btn = e.target.closest("[data-execute]");
    if (!btn || state.busy) {
      return;
    }
    const targets = state.executeTargets;
    if (targets.length === 0) {
      return;
    }
    const gen = state.mapGeneration;
    state.busy = true;
    syncControls();
    try {
      if (btn.dataset.execute === "here") {
        const origin = state.lastOrigin ?? state.pendingOrigin;
        if (!origin) {
          statusEl.textContent = "set a throw position first, or use \u201cFind a spot\u201d";
          return;
        }
        state.busy = false;
        await solveExecuteFrom(origin);
        return;
      } else {
        // Cold, this is a full map-wide solve per smoke; warm it is instant.
        statusEl.textContent = `looking for a spot that throws all ${targets.length}\u2026 (first time on a map this takes a while)`;
        const { error, data } = await findExecuteSpots(state.currentMap, targets);
        if (state.mapGeneration !== gen) { return; }
        if (error) { statusEl.textContent = error; return; }
        state.executeSpots = data;
        const n = data.spots.length;
        statusEl.textContent = n
          ? `${n} spot${n === 1 ? "" : "s"} can throw all ${targets.length} - pick one from the list to solve from it`
          : data.impossibleTargets.length
            ? `smoke ${data.impossibleTargets.map(i => i + 1).join(", ")} cannot be thrown at all - try moving it`
            : "no single spot can throw all of those - try fewer smokes, or move one";
        scheduleDraw();
        sync3d();
      }
    } finally {
      state.busy = false;
      syncControls();
    }
  });

  meshdiffBtn.addEventListener("click", async () => {
    const turningOn = !state.meshdiffOn;
    if (turningOn && !state.meshdiff) {
      meshdiffBtn.disabled = true;
      statusEl.textContent = "loading the mesh diff (one-time, several MB)…";
      try {
        state.meshdiff = await fetchMeshDiff(state.currentMap);
      } catch (err) {
        statusEl.textContent = `mesh diff unavailable: ${err.message}`;
        meshdiffBtn.disabled = false;
        return;
      }
      meshdiffBtn.disabled = false;
      statusEl.textContent = "";
      if (!state.meshdiff) {
        state.meshdiffAvailable = false;
        syncControls();
        return;
      }
    }
    state.meshdiffOn = turningOn;
    syncControls();
    syncMeshDiff3d();
    draw();
    current3d()?.focusStage();
  });
  // The full-screen "+" lineup crosshair: a numbered tick ruler (-5..5) on both
  // axes, so a grenade lines up by tick like CS2's grenade crosshair. Built once
  // as percentage-positioned ticks so it scales with the viewport, no resize
  // handler needed.
  // The aiming ruler, in real degrees off the aim axis, with CS2's own
  // grenade-crosshair marks called out.
  //
  // It used to place a tick every 8% of the viewport and label them 1..5. At
  // this camera (73.74 deg vertical, CS2's own Hor+ value for fov 90) that is
  // 6.84 degrees per division, so the tick labelled "2" sat 13.7 degrees below
  // the crosshair - and anyone comparing it against the game found the ruler
  // and the world disagreeing by most of a window.
  //
  // The long marks are the game's. Measured off a 1920x1080 in-game capture of
  // the grenade overlay, every one of its ticks sits within 0.12 degrees of a
  // multiple of ten - 1x is 10 degrees, 2x is 20, and so on - so "2y up" reads
  // the same here as it does in the game. (That measurement also confirms the
  // camera: the angles only come out round if the projection matches CS2's.)
  //
  // A tick's distance from the centre is tan(angle) over the tangent of the
  // half-FOV, so the whole thing has to be rebuilt whenever the viewport
  // changes shape: the vertical scale follows the fixed vertical FOV, and the
  // horizontal one widens with the aspect exactly as the game's does.
  const RULER_FINE = [1, 2, 3, 4, 5];
  const RULER_GAME = [10, 20, 30, 40, 50];
  function buildRuler(el) {
    const t3 = current3d();
    const fovY = t3 ? t3.camera.fov : verticalFovFromDesired(90);
    const aspect = t3 ? t3.camera.aspect : (stage3d.clientWidth || 16) / (stage3d.clientHeight || 9);
    const tanHalfY = Math.tan(fovY * Math.PI / 360);
    const tanHalfX = tanHalfY * aspect;
    // Rebuilt in place: the ruler is one element that outlives every camera.
    el.replaceChildren();
    el.dataset.fov = `${fovY.toFixed(2)}x${aspect.toFixed(3)}`;
    const frag = document.createDocumentFragment();
    frag.append(
      Object.assign(document.createElement("div"), { className: "rl-h" }),
      Object.assign(document.createElement("div"), { className: "rl-v" }));
    const rl = (cls, css, text) => {
      const d = document.createElement("div");
      d.className = cls;
      d.style.cssText = css;
      if (text !== undefined) { d.textContent = text; }
      return d;
    };
    const pctY = deg => 50 - 100 * Math.tan(deg * Math.PI / 180) / (2 * tanHalfY);
    const pctX = deg => 50 + 100 * Math.tan(deg * Math.PI / 180) / (2 * tanHalfX);
    for (const [angles, len, game] of [[RULER_FINE, 8, false], [RULER_GAME, 16, true]]) {
      for (const a of angles) {
        for (const deg of [a, -a]) {
          // The game names its marks by count and axis (1x across, 1y up); the
          // fine marks in between are plain degrees.
          const n = deg / 10;
          const labelX = game ? `${n}x` : String(deg);
          const labelY = game ? `${n}y` : String(deg);
          const cls = game ? "rl-tick rl-game" : "rl-tick";
          const numCls = game ? "rl-num rl-game" : "rl-num";
          const x = pctX(deg), y = pctY(deg);
          if (x > 1 && x < 99) {
            frag.append(rl(cls, `left:${x}%;top:calc(50% - ${len / 2}px);width:2px;height:${len}px;transform:translateX(-50%)`));
            frag.append(rl(numCls, `left:${x}%;top:calc(50% + ${len / 2 + 3}px);transform:translateX(-50%)`, labelX));
          }
          if (y > 1 && y < 99) {
            frag.append(rl(cls, `top:${y}%;left:calc(50% - ${len / 2}px);height:2px;width:${len}px;transform:translateY(-50%)`));
            frag.append(rl(numCls, `top:${y}%;left:calc(50% + ${len / 2 + 5}px);transform:translateY(-50%)`, labelY));
          }
        }
      }
    }
    el.append(frag);
  }

  // Fetched once per lineup and cached on it: a throw's arc is fixed for a given
  // map build, and only the selected one is ever drawn. A failure here is not
  // worth interrupting anyone over - the straight throw-spot-to-landing line is
  // still drawn, it just does not curve.
  async function loadPath(l) {
    if (l._path || l._pathFailed) {
      return;
    }
    try {
      l._path = (await fetchTrajectory(state.currentMap, l, brokenParam())).points;
    } catch {
      l._pathFailed = true;
      return;
    }
    if (state.result?.lineups[state.selected] === l) {
      draw();
      sync3d();
    }
  }

  function select(i) {
    state.selected = i === state.selected ? -1 : i;
    syncUrl();
    renderLineups();
    if (state.selected >= 0) {
      revealSelected();
      loadPath(state.result.lineups[state.selected]);
    }
    draw();
    sync3d();
  }

  // ONE legend renderer feeds both key chips (#key-overlays in 2D, #key-overlays-3d
  // in 3D) so an overlay reads the same plain-language color explanation
  // wherever it is visible, instead of two hand-maintained copies drifting
  // apart. `view` is the chip being painted; rows are withheld unless that
  // chip also belongs to the currently active view - both chips exist in the
  // DOM at once (the 2D marker legend stays up in 3D too, see keyEl below),
  // so without this a mesh-diff pair painted on the 3D scene would also show
  // up in the 2D chip sitting behind it, explaining a radar the user isn't
  // even looking at.
  function overlayLegendRows(view, in3d) {
    if ((view === "3d") !== in3d) {
      return [];
    }
    const rows = [];
    if (state.meshdiffOn && state.meshdiff?.cells.length) {
      rows.push(
        `<div class="key-row"><span class="swatch meshdiff-render"></span> real surface the solver can't see - your smoke flies through it</div>`,
        `<div class="key-row"><span class="swatch meshdiff-physics"></span> phantom surface only the solver has - your smoke bounces off nothing</div>`);
    }
    if (view === "3d" && state.collisionOn) {
      rows.push(`<div class="key-row"><span class="swatch phantom"></span> invisible collision - grenade-clips, physics-clips, glass that stop smokes</div>`);
    }
    if (state.targetsOn && state.targets.length) {
      rows.push(`<div class="key-row"><span class="key-label">targets</span><span><span class="dot named-target"></span> named</span><span><span class="dot named-target provisional"></span> ? provisional name</span> <span class="key-note">click one to smoke it</span></div>`);
    }
    if (state.spawnsOn && state.spawns) {
      rows.push(`<div class="key-row"><span class="key-label">spawns</span><span><span class="dot spawn-t"></span> T</span><span><span class="dot spawn-ct"></span> CT</span></div>`);
    }
    if (view === "2d" && state.prosmokesOn && state.prosmokes) {
      rows.push(`<div class="key-row"><span class="key-label">pro smokes</span><span><span class="dot prosmoke-throw"></span> thrown from</span><span><span class="dot prosmoke-land"></span> lands</span></div>`);
    }
    return rows;
  }

  // Paints one overlay-legend chip and reports whether it ended up with
  // anything in it, so the caller can fold that into the chip's own hidden
  // state (a target-less map can still have spawns or mesh diff switched on).
  function paintOverlayGroup(el, view, in3d) {
    const rows = overlayLegendRows(view, in3d);
    el.innerHTML = rows.length ? `<div class="key-subhead">overlays</div>${rows.join("")}` : "";
    el.hidden = rows.length === 0;
    return rows;
  }

  // Every control's state is a pure function of what the user has actually done,
  // so it is derived in one place rather than patched from each handler - which
  // is how "Target" ended up looking permanently pressed. A control that cannot
  // do anything yet is absent, not greyed out, so the card reads as a sequence.

  // A toggle has to say it is on to the eye AND to a screen reader; these used
  // to set only the class, so Collision/Top-down/Mesh diff/Spawns/Pro smokes
  // announced nothing about their state.
  const press = (el, on) => {
    el.classList.toggle("active", on);
    el.setAttribute("aria-pressed", String(on));
  };

  function syncControls() {
    const hasTarget = !!state.target;
    const in3d = stage3d.style.display !== "none";

    // Exactly one control is ever the filled primary: whatever the next step in
    // the sequence is. Pick a target, then search from it, then the tool has
    // nothing to urge and everything goes quiet.
    // Each button reports the state of its own half of the question: waiting
    // to be answered, listening for a map click, or answered with a tick that
    // still re-arms if clicked again.
    pickBtn.textContent = state.picking ? "Click the map…" : hasTarget ? "Target set" : "Set target";
    pickBtn.classList.toggle("armed", state.picking);
    pickBtn.classList.toggle("done", hasTarget && !state.picking);
    pickBtn.classList.toggle("primary", !state.picking && !hasTarget);
    pickBtn.title = hasTarget ? "Target is set - click to pick a different one" : "Click the map to place the smoke target";

    const throwSpot = state.pendingOrigin ?? state.lastOrigin;
    pickOriginBtn.textContent = state.pickingOrigin ? "Click the map…"
      : throwSpot ? "Throw position set" : "Set throw position";
    pickOriginBtn.classList.toggle("armed", state.pickingOrigin);
    pickOriginBtn.classList.toggle("done", !!throwSpot && !state.pickingOrigin);
    pickOriginBtn.title = throwSpot
      ? "Throwing from this spot - click to pick a different one"
      : "Click the map to place the throw spot - leave it unset to search every spot on the map";

    // The boxes mirror whatever is set, so the same field reads a position out
    // as pastes one in. Never while it is being typed into.
    if (document.activeElement !== targetIn) {
      targetIn.value = state.target ? setposOf(state.target, state.result?.target?.[2]) : "";
    }
    if (document.activeElement !== originIn) {
      originIn.value = throwSpot ? setposOf(throwSpot, state.result?.lineups?.[0]?.feet?.[2]) : "";
    }
    copyTargetBtn.hidden = !hasTarget;
    copyOriginBtn.hidden = !throwSpot;

    // One control, three answers to "where do I look": the spot above, the
    // whole map, or just the spawns. Solving the pair needs to be reachable at
    // all - re-picking either half used to leave a target and a throw spot on
    // screen with nothing that would put them together again.
    searchSeg.hidden = !hasTarget;
    searchLabel.hidden = !hasTarget;
    // Recomputed from scratch every pass, never OR'd onto what the button
    // already was: `disabled = disabled || busy` is a latch, so the first solve
    // (or floor chooser) turned Map and Spawns off for the rest of the session.
    // A search started before the floor is settled would sweep the wrong one,
    // and both spot searches need a spot to search from.
    for (const b of searchSeg.children) {
      const needsSpot = b.dataset.search === "exact" || b.dataset.search === "spot";
      b.disabled = (needsSpot && !throwSpot) || state.busy || state.awaitingLevel;
      // The likely next step reads as the action: the spot when one is set,
      // the map when the target is still on its own.
      b.classList.toggle("primary", !state.busy &&
        (throwSpot ? b.dataset.search === "spot" : b.dataset.search === "map" && !state.result));
    }
    searchBtnFor("spawns").hidden = !(state.spawns && (state.spawns.t.length || state.spawns.ct.length));
    clearBtn.hidden = !hasTarget;

    heatBtn.hidden = !state.result?.coverage;
    press(heatBtn, state.heatOn);
    // The button walks off -> coverage -> stand spots -> off; its label names
    // the view it is currently showing so the cycle is legible.
    heatBtn.textContent = !state.heatOn ? "Heatmap" : state.heatSpots ? "Heatmap: stand spots" : "Heatmap: coverage";
    document.getElementById("key-heat-cover").hidden = state.heatSpots;
    document.getElementById("key-heat-spots").hidden = !state.heatSpots;

    // Exactly one view is current: 2D, the collision mesh, or the textured map.
    const currentView = !in3d ? "2d" : current3d()?.isTextured ? "textured" : "3d";
    for (const b of viewSeg.children) {
      const on = b.dataset.view === currentView;
      b.classList.toggle("active", on);
      b.setAttribute("aria-pressed", String(on));
    }
    // A map with no textured export (de_boulder: the exporter crashes on it)
    // offers no tile for it rather than a tile that fails when pressed.
    viewBtn("textured").hidden = !(mapList?.find(m => m.map === state.currentMap)?.hasTextured ?? true);
    spawnsBtn.hidden = !(state.spawns && (state.spawns.t.length || state.spawns.ct.length));
    press(spawnsBtn, state.spawnsOn);
    targetsBtn.hidden = state.targets.length === 0;
    press(targetsBtn, state.targetsOn);
    // 2D only: the pro-demo density heatmap is painted on the radar canvas and
    // has no 3D representation, so offering it in the 3D view would promise
    // something that cannot appear there.
    proSmokesBtn.hidden = in3d || !(state.prosmokes && (state.prosmokes.throws.length || state.prosmokes.lands.length));
    press(proSmokesBtn, state.prosmokesOn);
    // The T/CT filter only makes sense while the heatmap is on.
    proSideSeg.hidden = proSmokesBtn.hidden || !state.prosmokesOn;
    for (const b of proSideSeg.children) {
      press(b, b.dataset.side === state.proSide);
    }
    // 2D's "recenter" is Reset view.
    resetViewBtn.hidden = in3d;
    // Collision and Top-down only mean something inside a 3D view; the aiming
    // overlay draws over it too. Disabled rather than hidden so the row keeps
    // its shape and the reason is one hover away.
    for (const b of [collisionBtn, topDownBtn]) {
      b.disabled = !in3d;
    }
    document.getElementById("reticle-seg").hidden = !in3d;
    document.querySelector('.action-label[data-for="reticle-seg"]').hidden = !in3d;
    for (const b of reticleBtns) {
      const on = b.dataset.reticle === state.reticleMode;
      b.classList.toggle("active", on);
      b.setAttribute("aria-pressed", String(on));
    }
    press(collisionBtn, in3d && state.collisionOn);
    press(topDownBtn, state.topDownOn);
    meshdiffBtn.hidden = !(state.meshdiffAvailable || state.meshdiff?.cells.length);
    press(meshdiffBtn, state.meshdiffOn);
    press(coverageBtn, state.coverageOn);
    // The execute block appears once there is a target to add, and the solve
    // row once there is something to solve.
    const anyExec = state.executeTargets.length > 0;
    executeCount.textContent = anyExec ? `(${state.executeTargets.length})` : "";
    executeClear.hidden = !anyExec;
    executeHint.hidden = anyExec;
    const execFull = state.executeTargets.length >= MAX_EXECUTE_SMOKES;
    executeAdd.disabled = state.busy || !hasTarget || execFull;
    executeAdd.textContent = execFull
      ? `${MAX_EXECUTE_SMOKES} smokes - one per player`
      : hasTarget
        ? `Add ${state.targetName ?? "current target"} to execute`
        : "Add current target to execute";
    executeAdd.title = execFull
      ? `An execute holds ${MAX_EXECUTE_SMOKES} smokes at most: one per player in the round`
      : hasTarget
        ? "Add the current target as the next smoke of this execute"
        : "Click the map to set a target first";
    executeList.hidden = !anyExec;
    executeSolveLabel.hidden = !anyExec;
    executeSeg.hidden = !anyExec;
    for (const b of executeSeg.children) { b.disabled = state.busy; }
    renderExecuteList();
    renderExecuteSpots();
    savedCountEl.textContent = state.saved.length ? `(${state.saved.length})` : "";
    document.body.classList.toggle("crosshair-3d", in3d && state.crosshairOn);
    rulerEl.hidden = !(in3d && state.reticleOn);

    // The overlay legend: same rows in both chips, only the overlays actually
    // switched on right now.
    const overlayRows2d = paintOverlayGroup(document.getElementById("key-overlays"), "2d", in3d);
    paintOverlayGroup(document.getElementById("key-overlays-3d"), "3d", in3d);

    // The marker/click/movement legend only means anything once there is a
    // result to explain; the overlay legend above has no such requirement, so
    // the chip as a whole stays visible for either reason on its own.
    document.getElementById("key-markers").hidden = !hasTarget;
    keyEl.hidden = !hasTarget && overlayRows2d.length === 0;
    keyEl.classList.toggle("heat", state.heatOn);

    // The 3D controls chip lives on the stage, not in the ephemeral status
    // line, so the bindings stay findable after the status text moves on.
    // Auto-open once per browser; a chip after that.
    const key3d = document.getElementById("key-3d");
    key3d.hidden = !in3d;
    if (in3d && !localStorage.getItem("smokesolver.seen3dHelp")) {
      key3d.open = true;
      localStorage.setItem("smokesolver.seen3dHelp", "1");
    }

    // A collapsed filters card silently hiding active filters would be a trap:
    // the result count would look wrong with no visible cause. A filter that
    // ships pre-set (sky) only counts when moved OFF its default.
    const active = filterEls.filter(f => f.value !== (f.dataset.default ?? "")).length;
    document.getElementById("filter-count").textContent = active ? `(${active})` : "";
    const adv = Object.keys(advancedParams()).length;
    document.getElementById("advanced-count").textContent = adv ? `(${adv})` : "";
  }

  // Heat mode swaps the map key from marker shapes to coverage colors (L23).
  function setHeat(on) {
    state.heatOn = on;
    syncControls();
  }

  // The one definition of "clear the search state": every field added here
  // (picking, results, selection, heat) used to be reset by hand at three
  // call sites, which is exactly how a new field gets missed at one of them.
  function resetSearch({ keepTarget = false } = {}) {
    state.picking = false;
    if (!keepTarget) {
      state.target = null;
    }
    state.result = null;
    state.selected = -1;
    state.heatOn = false;
    state.heatSpots = false;
    canvas.classList.remove("picking");
  }

  // Flag lineups thrown from (near) a player spawn so the card can badge them
  // [spawn] - the "can I smoke this off spawn?" question. The map sweep samples
  // a grid rather than landing exactly on each spawn, so ~48u (about one player
  // width) counts a stand spot as "at" a spawn.
  const SPAWN_LINEUP_RADIUS = 48;
  function tagSpawnLineups(lineups) {
    const spawns = state.spawns ? [...state.spawns.t, ...state.spawns.ct] : [];
    for (const l of lineups) {
      l._spawn = spawns.some(s => Math.hypot(s[0] - l.feet[0], s[1] - l.feet[1]) <= SPAWN_LINEUP_RADIUS);
    }
  }

  // A different map is entirely different geometry/nav/lineups, so this is a
  // full reset (target, results, 3D scene) rather than an incremental swap -
  // there is only ever "the current map," never a per-map cache to switch
  // back into. Returns false (and shows the boot error) if the map's data
  // failed to load, so the caller can bail out the same way initial boot did.
  async function loadMap(name) {
    // A newer loadMap (or any other map-scoped async load) invalidates this one:
    // capture the generation now and bail after each await if it moved, so two
    // overlapping switches can't interleave their commits. The map dropdown fires
    // rapid change events on wheel/arrow, so this is ordinary UI, not an edge case.
    const gen = ++state.mapGeneration;
    solveController?.abort();
    teardown3d();
    stage3d.style.display = "none";
    canvas.style.display = "block";
    state.currentMap = name;
    state.spawns = null;
    state.pendingOrigin = null;
    state.spawnsOn = false;
    state.topDownOn = false;
    state.prosmokes = null;
    state.prosmokesOn = false;
    state.meshdiff = null;
    state.meshdiffAvailable = false;
    state.meshdiffOn = false;
    localStorage.setItem("smokesolver.lastMap", name);
    resetSearch();
    syncUrl();
    syncControls();

    let mapData;
    try {
      mapData = await loadMapData(name);
    } catch {
      if (state.mapGeneration !== gen) { return false; }
      bootError(`data/${name}.viewer-map.json`);
      return false;
    }
    if (state.mapGeneration !== gen) { return false; }
    state.mapData = mapData;
    // Spawns are a bonus overlay: fetch without blocking the map load, and
    // reveal the toggle once they arrive (syncControls hides it when absent).
    fetchSpawns(name).then(s => {
      if (state.mapGeneration !== gen) { return; }
      state.spawns = s;
      // A solve may have finished before spawns arrived; tag and redraw so its
      // spawn lineups get their badge without needing a re-solve.
      if (state.result?.lineups) { tagSpawnLineups(state.result.lineups); renderLineups(); }
      syncControls();
    }).catch(e => console.warn("spawns unavailable for", name, e));
    loadFavorites(name);
    // Named targets come with the map; a click that lands near one snaps to
    // it, so the labels below need them in place before the first click.
    state.targets = [];
    state.targetName = null;
    fetchTargets(name).then(t => {
      if (state.mapGeneration !== gen) { return; }
      state.targets = disambiguateTargetNames(Array.isArray(t) ? t : []);
      // A target that arrived by link, before the names did, gets its name now.
      if (state.target && !state.targetName) {
        state.targetName = nearestNamedTarget(state.target)?.name ?? null;
        syncControls();
      }
      scheduleDraw();
      sync3d();
    }).catch(e => console.warn("targets unavailable for", name, e));
    // Optional per-map data is asked for only when /api/maps says it exists.
    const extras = mapList.find(m => m.map === name);
    if (extras?.hasProSmokes) {
      fetchProSmokes(name).then(d => {
        if (state.mapGeneration === gen) { state.prosmokes = d; syncControls(); }
      }).catch(e => console.warn("pro smokes unavailable for", name, e));
    }
    // Dev overlay: absent on most maps (only meshdiff CLI runs produce it), so
    // a 404 is the expected default, not a failure worth logging. Only its
    // existence is checked here; the payload carries every mismatched surface
    // and is fetched when someone actually switches the overlay on.
    if (extras?.hasMeshDiff) {
      meshDiffExists(name).then(has => {
        if (state.mapGeneration === gen) { state.meshdiffAvailable = has; syncControls(); }
      }).catch(() => {});
    }
    try {
      await loadRadar();
    } catch {
      if (state.mapGeneration !== gen) { return false; }
      bootError("data/" + state.mapData.image);
      return false;
    }
    if (state.mapGeneration !== gen) { return false; }
    clearBootError();
    recolorRadar();
    resize();
    resetView();
    renderLineups();
    statusEl.textContent = "";
    return true;
  }

  try {
    mapList = await loadMapList();
  } catch {
    bootError("/api/maps");
    return;
  }
  if (mapList.length === 0) {
    bootError("data/*.s2geo (no maps extracted yet)");
    return;
  }
  // A map with no nav mesh has no walkable ground to sweep, so it can never
  // return a lineup - that is the simulation test bed (flatgrass), not something
  // to offer a player. Still reachable by an explicit ?map= for testing.
  const playable = mapList.filter(m => m.hasLineups);
  mapSelect.innerHTML = playable.map(m => `<option value="${esc(m.map)}">${esc(m.map)}</option>`).join("");
  mapSelect.addEventListener("change", () => loadMap(mapSelect.value));
  readColors();

  // Land somewhere deliberate: an explicit ?map= wins, otherwise the map picked
  // last visit, and only a genuinely first-time visitor gets the intro. Skipping
  // it for returning users is the point - it is onboarding, not a gate.
  const LAST_MAP_KEY = "smokesolver.lastMap";
  const known = name => mapList.some(m => m.map === name);
  const urlMap = new URLSearchParams(location.search).get("map");
  const savedMap = localStorage.getItem(LAST_MAP_KEY);
  const initialMap = known(urlMap) ? urlMap : known(savedMap) ? savedMap : null;
  // ?map=flatgrass is the deliberate escape hatch to the test bed; give the
  // switcher an entry for it so it does not sit there showing the wrong map.
  if (initialMap && !playable.some(m => m.map === initialMap)) {
    mapSelect.insertAdjacentHTML("beforeend", `<option value="${esc(initialMap)}">${esc(initialMap)}</option>`);
  }

  // Minimal modal discipline shared by the intro and the preview modal: focus
  // moves in on open, Tab cycles inside, Escape closes (via the dialog's own
  // close path), and focus returns to whatever opened it. Without this a
  // keyboard or screen-reader user tabs straight into the page behind an
  // open "modal" and never finds it.
  function openModal(el, onEscape) {
    el._returnFocus = document.activeElement;
    el._keydown = e => {
      if (e.key === "Escape") {
        e.stopPropagation();
        onEscape?.();
        return;
      }
      if (e.key !== "Tab") {
        return;
      }
      const focusables = [...el.querySelectorAll("button, [href], select, input, [tabindex]:not([tabindex='-1'])")]
        .filter(f => !f.hidden && f.offsetParent !== null);
      if (focusables.length === 0) {
        return;
      }
      const first = focusables[0], last = focusables.at(-1);
      if (e.shiftKey && document.activeElement === first) {
        e.preventDefault();
        last.focus();
      } else if (!e.shiftKey && document.activeElement === last) {
        e.preventDefault();
        first.focus();
      }
    };
    el.addEventListener("keydown", el._keydown);
    el.hidden = false;
    (el.querySelector("button, [href], select, input") ?? el).focus();
  }
  function closeModal(el) {
    el.hidden = true;
    el.removeEventListener("keydown", el._keydown);
    el._returnFocus?.focus?.();
    el._returnFocus = null;
  }

  const intro = document.getElementById("intro");
  const introMapStep = document.getElementById("intro-map");
  const introFilterStep = document.getElementById("intro-filters");
  const filterBody = document.getElementById("filter-body");

  // The intro borrows the real <select> elements rather than cloning them, so
  // there is only ever one source of truth for a filter's value; they are handed
  // back to the sidebar card, in order, when it closes. Which means the borrowing
  // must only happen if the intro is actually going to be shown - doing it up
  // front left every returning visitor, and every ?map= link, with the filters
  // stranded inside a hidden dialog and an empty filters card.
  const filterEls = Object.values(state.filters);
  const filterRowsHtml = () => filterEls
    .map(f => `<div class="filter-row" data-for="${f.id}">` +
      `<label class="filter-head" for="${f.id}">` +
      `<b class="filter-info" tabindex="0" role="button" aria-label="What does ${esc(f.dataset.label)} do?">${esc(f.dataset.label)}:</b>` +
      `<span class="filter-slot"></span></label>` +
      `<p class="filter-desc">${esc(f.dataset.desc)}</p></div>`)
    .join("");
  // The label+description rows render in the sidebar too, permanently - they
  // used to exist only inside the intro, so the one explanation of what
  // "reliability" or "sky aim" means vanished forever the moment it closed
  // (and returning visitors never saw it at all).
  // Each title carries a dotted underline; its explanation pops up on hover or
  // keyboard focus, and a click pins it open (touch has no hover). One delegated
  // set of handlers serves every context (sidebar, intro, advanced card).
  const descOf = el => el.closest(".filter-row")?.querySelector(".filter-desc");
  // Only ever one explanation open at a time - stacking them (as happened on
  // touch, where tapping several labels pinned several popups) was unreadable.
  // Visibility rides the .open class so it can fade+slide in via CSS rather than
  // snapping on. A pinned one (tapped open) survives the pointer leaving.
  const closeAllDescs = except => {
    for (const d of document.querySelectorAll(".filter-desc.open")) {
      if (d !== except) { d.classList.remove("open", "pinned"); }
    }
  };
  const showDesc = el => { const d = descOf(el); if (d) { closeAllDescs(d); d.classList.add("open"); } };
  const hideDesc = el => { const d = descOf(el); if (d && !d.classList.contains("pinned")) { d.classList.remove("open"); } };
  document.addEventListener("pointerover", e => {
    if (e.target instanceof Element && e.target.classList.contains("filter-info")) { showDesc(e.target); }
  });
  document.addEventListener("pointerout", e => {
    if (e.target instanceof Element && e.target.classList.contains("filter-info")) { hideDesc(e.target); }
  });
  document.addEventListener("focusin", e => {
    if (e.target instanceof Element && e.target.classList.contains("filter-info")) { showDesc(e.target); }
  });
  document.addEventListener("focusout", e => {
    if (e.target instanceof Element && e.target.classList.contains("filter-info")) { hideDesc(e.target); }
  });
  document.addEventListener("click", e => {
    if (!(e.target instanceof Element) || !e.target.classList.contains("filter-info")) {
      // A tap anywhere else dismisses a pinned explanation.
      closeAllDescs(null);
      return;
    }
    e.preventDefault();
    const desc = descOf(e.target);
    if (desc) {
      const pin = !desc.classList.contains("pinned");
      closeAllDescs(desc);
      desc.classList.toggle("pinned", pin);
      desc.classList.toggle("open", pin);
    }
  });
  filterBody.innerHTML = filterRowsHtml();
  const slotFor = (container, f) => container.querySelector(`.filter-row[data-for="${f.id}"] .filter-slot`);
  for (const f of filterEls) {
    slotFor(filterBody, f).appendChild(f);
  }
  function mountIntroFilters() {
    document.getElementById("intro-filter-rows").innerHTML = filterRowsHtml();
    for (const f of filterEls) {
      slotFor(document.getElementById("intro-filter-rows"), f).appendChild(f);
    }
  }

  function closeIntro() {
    for (const f of filterEls) {
      slotFor(filterBody, f).appendChild(f);
    }
    closeModal(intro);
    syncControls();
    statusEl.textContent = "click anywhere on the map to set your smoke target";
  }

  document.getElementById("intro-map-grid").innerHTML = playable
    .map(m => `<button type="button" class="map-pick" data-map="${esc(m.map)}" title="${esc(m.map)}">` +
      // Root-absolute: a url() inside a custom property resolves against the
      // stylesheet that consumes it (viewer/app.css), not the document.
      `<span class="thumb" style="--thumb:url('/data/${esc(m.map)}.thumb.png')"></span>` +
      `${esc(m.map.replace(/^de_/, ""))}</button>`)
    .join("");
  for (const b of document.querySelectorAll(".map-pick")) {
    b.addEventListener("click", async () => {
      if (!(await loadMap(b.dataset.map))) {
        closeIntro();
        return;
      }
      mapSelect.value = b.dataset.map;
      introMapStep.hidden = true;
      introFilterStep.hidden = false;
    });
  }
  document.getElementById("intro-done").addEventListener("click", closeIntro);
  document.getElementById("intro-back").addEventListener("click", () => {
    introFilterStep.hidden = true;
    introMapStep.hidden = false;
  });

  if (initialMap) {
    mapSelect.value = initialMap;
    const bootParams = new URLSearchParams(location.search);
    if (!(await loadMap(initialMap))) {
      return;
    }
    applyPermalink(bootParams);
  } else {
    mountIntroFilters();
    // Escape may only skip the intro once a map exists - it is the map picker,
    // and closing it with nothing loaded would strand the page empty.
    openModal(intro, () => { if (state.mapData) { closeIntro(); } });
  }

  // Progress lines stream in every ~100ms; painting each batch as dots shows
  // both sweep speed and which standable origins were actually evaluated.
  function onSolveProgress(msg) {
    const p = state.progress;
    if (!p) {
      return;
    }
    if (msg.phase === "queued") {
      statusEl.textContent = msg.count > 0
        ? `waiting for a free solver slot - ${msg.count} solve${msg.count === 1 ? "" : "s"} ahead of you…`
        : "waiting for a free solver slot…";
    } else if (msg.phase === "prepare") {
      statusEl.textContent = "preparing voxel grid…";
    } else if (msg.phase === "sweep") {
      p.phase = "sweep";
      p.total = msg.count;
    } else if (msg.phase === "exhaustive") {
      // The exact-spot solve's last resort: the real simulator over every
      // angle and every kind of throw from this one spot.
      statusEl.textContent = "nothing on the fast pass - trying every angle of every throw from this exact spot in the full sim…";
    } else if (msg.phase === "verify") {
      p.phase = "verify";
      p.candidates = msg.count;
      statusEl.textContent = `verifying 0 / ${msg.count} candidates against the exact sim…`;
    } else if (msg.checked) {
      p.checked.push(...msg.checked);
      // Naming what is being counted matters: the total is every spot a player
      // can stand within throw range of the target, not the whole map, which is
      // why it lands near the same figure regardless of how big the map is.
      const ofTotal = p.total ? ` / ${p.total}` : "";
      statusEl.textContent = `checked ${p.checked.length}${ofTotal} stand spots in throw range…`;
    } else if (msg.verified) {
      p.verified.push(...msg.verified);
      statusEl.textContent = `verifying ${p.verified.length} / ${p.candidates ?? "?"} candidates against the exact sim…`;
    }
    scheduleDraw();
    syncProgress3d();
  }

  // Solver-side knobs from the advanced card: unlike the display filters,
  // these change what gets COMPUTED, so they ride along with each solve
  // request. Only non-defaults are sent; the server owns the defaults.
  function advancedParams() {
    const p = {};
    const tol = document.getElementById("a-tolerance").value;
    if (tol) { p.tolerance = Number.parseFloat(tol); }
    const reach = document.getElementById("a-reach").value;
    if (reach === "exact") {
      // Only the clicked spot and its wall pins: the smallest reach the API
      // accepts, with the fine angle lattice and the verify gate opened to its
      // floor, so an awkward-but-real throw from that exact position is found
      // rather than ranked away. This is the "I know where I want to stand"
      // search, and it is deliberately slower per spot.
      p.originReach = 16;
      p.fineScan = true;
      p.minStability = 0.05;
    } else if (reach) {
      p.originReach = Number.parseFloat(reach);
    }
    const stab = document.getElementById("a-stability").value;
    if (stab) { p.minStability = Number.parseFloat(stab); }
    if (document.getElementById("a-scan").value === "fine") { p.fineScan = true; }
    const world = document.getElementById("a-world").value;
    if (world) { p.broken = world.split(","); }
    return p;
  }

  // Reset to defaults. The defaults are whatever the markup declares (the
  // `selected` option, the `checked` box), so this restores from the document
  // instead of keeping a second list of defaults that can drift from it.
  function resetCard(root) {
    for (const sel of root.querySelectorAll("select")) {
      // data-default is the value the filter counter treats as "untouched", so
      // it is what reset has to restore; fall back to the markup's selected
      // option, then to the first one.
      const fallback = [...sel.options].find(o => o.defaultSelected) ?? sel.options[0];
      sel.value = sel.dataset.default ?? fallback.value;
      sel.dispatchEvent(new Event("change", { bubbles: true }));
    }
    for (const box of root.querySelectorAll("input[type=checkbox]")) {
      box.checked = box.defaultChecked;
      box.dispatchEvent(new Event("change", { bubbles: true }));
    }
  }
  document.getElementById("filters-reset").addEventListener("click", e => {
    // The summary is a toggle; resetting should not also collapse the card.
    e.preventDefault();
    e.stopPropagation();
    resetCard(document.getElementById("filter-body"));
    renderLineups();
    draw();
    statusEl.textContent = "filters reset";
  });
  document.getElementById("advanced-reset").addEventListener("click", e => {
    e.preventDefault();
    e.stopPropagation();
    resetCard(document.getElementById("advanced-body"));
    statusEl.textContent = "advanced settings reset - they apply to your next search";
  });

  // Simple by default. The row answers the four questions a lineup guide
  // answers; whoever wants bounce counts and miss distances on every line can
  // say so once and have it remembered.
  const rowDetailBtn = document.getElementById("row-detail");
  press(rowDetailBtn, state.expertRows);
  rowDetailBtn.setAttribute("aria-pressed", String(state.expertRows));
  rowDetailBtn.addEventListener("click", () => {
    state.expertRows = !state.expertRows;
    try { localStorage.setItem("smoke.expertRows", state.expertRows ? "1" : "0"); } catch { /* private mode */ }
    press(rowDetailBtn, state.expertRows);
    rowDetailBtn.setAttribute("aria-pressed", String(state.expertRows));
    renderLineups();
  });

  document.getElementById("sort-by").addEventListener("change", () => renderLineups());

  document.getElementById("a-pro").addEventListener("change", e => {
    // Ranking-only: it changes the order and the score, never which lineups
    // the solver found, so nothing needs re-solving.
    state.proWeighting = e.target.checked;
    renderLineups();
  });

  document.getElementById("a-world").addEventListener("change", () => {
    current3d()?.setWorldState(brokenParam());
    syncControls();
  });

  // The world state a lineup was solved under must follow it into every later
  // fetch: an arc drawn against intact glass for a broken-glass lineup would
  // show a bounce the throw does not have.
  function brokenParam() {
    const world = document.getElementById("a-world").value;
    return world || undefined;
  }

  // Rough solve-cost multiplier of the current advanced settings: fine scan
  // sweeps ~3x the angles, and probe origins grow with the square of the
  // search radius. Used only to warn, never to block.
  function advancedCostFactor() {
    const p = advancedParams();
    let factor = p.fineScan ? 3 : 1;
    if (p.originReach) {
      factor *= Math.max((p.originReach / 300) ** 2, 0.1);
    }
    return factor;
  }
  function advancedCostNote() {
    const factor = advancedCostFactor();
    const note = document.getElementById("advanced-note");
    note.textContent = "These change how the solver searches - they apply to your next Search / spot click." +
      (factor >= 2 ? ` Current settings make each solve roughly ${Math.round(factor)}x slower.` : "");
  }

  // The movement and click filters, when set BEFORE solving, scope the sweep
  // itself: the solver skips every other type/strength combination from every
  // origin (up to 15x less work), instead of computing everything and hiding
  // most of it. state.solveScope remembers it so the status line can say the
  // results were solved narrow.
  function solveScopeParams() {
    const p = {};
    const type = state.filters.type.value;
    if (type) { p.types = [type]; }
    const strength = state.filters.strength.value;
    if (strength) { p.strengths = [Number.parseFloat(strength)]; }
    return p;
  }

  async function runQuery(body) {
    // The spot a one-spot probe solved from, kept so it can be copied back out
    // - otherwise a position you found by clicking is impossible to quote.
    state.lastOrigin = body.origin ?? null;
    // The sidebar's throw-position box reads from this, so it has to be told
    // now rather than when the solve finishes half a minute later.
    state.pendingOrigin = null;
    syncControls();
    // A second dispatch (rapid 3D taps outrun the busy flag) must supersede
    // the first, not orphan it: abort it so it cannot finish later and paint
    // a stale result, and remember which map this solve belongs to.
    solveController?.abort();
    const gen = state.mapGeneration;
    const controller = new AbortController();
    state.busy = true;
    state.progress = { phase: "sweep", total: 0, candidates: 0, checked: [], verified: [] };
    const cost = advancedCostFactor();
    statusEl.textContent = cost >= 2 ? `solving… (advanced settings ≈ ${Math.round(cost)}x slower)` : "solving…";
    solveController = controller;
    cancelBtn.hidden = false;
    try {
      const scope = solveScopeParams();
      state.solveScope = Object.keys(scope).length ? scope : null;
      const { error, data } = await postLineupQuery({ ...advancedParams(), ...scope, ...body, map: state.currentMap }, controller.signal, onSolveProgress);
      if (state.mapGeneration !== gen || solveController !== controller) {
        return;
      }
      if (error) {
        statusEl.textContent = error;
        return;
      }
      const next = data;
      if (next.lineups.length === 0) {
        // The server knows WHICH kind of empty this is - a target resolved
        // inside geometry reads nothing like "nothing reaches there" - so say
        // what it said, and only fall back to guessing when it did not.
        // A single-origin probe checked one spot, not the map; saying "any of
        // N stand spots" for it would wrongly read as an exhaustive sweep.
        statusEl.textContent = next.emptyReason
          ? body.origin
            ? `${next.emptyReason} - "Search the whole map" sweeps every spot that can`
            : next.emptyReason
          : body.origin
            ? `no throw from that spot reaches the target - "Search the whole map" sweeps every spot that can`
            : `no throw reaches there from any of the ${next.origins} stand spots in range - try another target`;
        return;
      }
      next.lineups.forEach((l, i) => { l._idx = i; l._favorite = isFavorite(l); });
      tagSpawnLineups(next.lineups);
      state.result = next;
      state.votes = null;
      loadVotes();
      // Adopt the server's resolved target, which carries the ground Z it snapped
      // a 2D (Z-less) pick onto. Keeps the 2D and 3D target at the same height the
      // sim actually used, so switching views no longer moves or floats it.
      if (Array.isArray(next.target) && next.target.length > 2) {
        state.target = next.target;
        syncUrl();
      }
      state.selected = -1;
      renderLineups();
      sync3d();
    } catch (err) {
      if (solveController === controller) {
        statusEl.textContent = err.name === "AbortError" ? "cancelled" : `error: ${err.message}`;
      }
    } finally {
      // A superseded solve must not tear down the state its replacement owns.
      if (solveController === controller) {
        state.busy = false;
        state.progress = null;
        solveController = null;
        cancelBtn.hidden = true;
        syncControls();
        draw();
        syncProgress3d();
      }
    }
  }

  // Shareable permalinks. t=x,y,z is the resolved target; l identifies the
  // selected lineup by its PHYSICAL parameters (type:strength:fx:fy:fz:
  // pitch:yaw) - result indices are unstable across data redeploys, the
  // physics is the lineup's identity. The address bar always holds a valid
  // share link for the current view.
  function permalink() {
    const p = new URLSearchParams({ map: state.currentMap });
    if (state.target) {
      p.set("t", state.target.map(v => Math.round(v * 10) / 10).join(","));
    }
    const l = state.selected >= 0 ? state.result?.lineups[state.selected] : null;
    if (l) {
      // The run direction is part of the physics identity; appended only when
      // set so links minted before it existed keep their exact old shape.
      const parts = [l.type, l.strength, ...l.feet.map(v => Math.round(v * 10) / 10), l.pitch.toFixed(2), l.yaw.toFixed(2)];
      if (l.runDeg) {
        parts.push(l.runDeg.toFixed(0));
      }
      p.set("l", parts.join(":"));
    }
    return `${location.pathname}?${p}`;
  }
  function syncUrl() {
    history.replaceState(null, "", permalink());
  }

  async function shareLineup() {
    const url = location.origin + permalink();
    if (navigator.share) {
      try {
        await navigator.share({ url });
        return;
      } catch {
        // Cancelled share sheets fall through to the clipboard path.
      }
    }
    try {
      await navigator.clipboard.writeText(url);
      statusEl.textContent = "link copied - it opens this exact lineup";
    } catch {
      // Clipboard denied/unfocused: show the URL so the link is still gettable.
      statusEl.textContent = `copy failed - link: ${url}`;
    }
  }

  // Landing on a shared link: set the target, then render the ONE throw the
  // link fully describes - no sweeping the map for other spots the sharer did
  // not point at. A target-only link (no `l`) just lands on the target with
  // "Search the whole map" queued up.
  async function applyPermalink(params) {
    const t = params.get("t")?.split(",").map(Number);
    if (!t || t.length < 2 || t.some(v => !Number.isFinite(v))) {
      return;
    }
    setTarget(t, `target ${t[0].toFixed(0)}, ${t[1].toFixed(0)} (from link)`);
    const parts = params.get("l")?.split(":");
    if (!parts || parts.length < 7 || parts.length > 8) {
      return;
    }
    const type = parts[0];
    const [strength, fx, fy, fz, pitch, yaw] = parts.slice(1, 7).map(Number);
    const runDeg = parts.length === 8 ? Number(parts[7]) : 0;
    if ([strength, fx, fy, fz, pitch, yaw, runDeg].some(v => !Number.isFinite(v))) {
      return;
    }
    await showSingleLineup({ type, strength, feet: [fx, fy, fz], pitch, yaw, runDeg });
  }

  // Render exactly one lineup from its physical spec: the server analyzes just
  // that throw (path, rest, aim reference, pin, scatter) and the viewer shows
  // it selected, bypassing the filters since it is an explicit pick. "Search
  // map" is still one click away for anyone who wants the alternatives.
  async function showSingleLineup(spec) {
    state.busy = true;
    statusEl.textContent = "loading the shared lineup…";
    syncControls();
    try {
      const { points, lineup } = await fetchLineupOne(state.currentMap, state.target, spec, brokenParam());
      lineup._idx = 0;
      lineup._path = points; // pre-set so select() draws without a second fetch
      state.result = { target: [...state.target], origins: 0, coverage: null, lineups: [lineup], single: true };
      state.selected = -1;
      select(0);
      statusEl.textContent = `showing the shared lineup · "Search the whole map" finds other spots for this target`;
    } catch (err) {
      statusEl.textContent = err.name === "AbortError"
        ? "cancelled"
        : `could not load the shared lineup (${err.message}) - "Search the whole map" to solve the target`;
    } finally {
      state.busy = false;
      syncControls();
    }
  }

  // A spawn clicked before any target is a stated intention - "throw from
  // here" - so it is held rather than discarded, and spent the moment the
  // target it was waiting for arrives.
  function usePendingOrigin() {
    const origin = state.pendingOrigin;
    if (!origin || !state.target) {
      return;
    }
    state.pendingOrigin = null;
    runQuery({ target: state.target, origin });
  }

  function pickOrigin(origin, team) {
    state.pendingOrigin = origin;
    statusEl.textContent =
      `throwing from ${team} spawn - now click the spot you want to smoke`;
    draw();
    sync3d();
  }

  // Provisional names come from the nearest callout, and one callout can
  // have several smoke clusters around it - two pins both called "near
  // MidDoors" tell nobody which is which, in the list or on the map. Until a
  // person names them, the later ones get a number. Ids are untouched, so
  // votes and saved lineups still land on the right pin.
  function disambiguateTargetNames(targets) {
    const seen = new Map();
    for (const t of targets) {
      const n = (seen.get(t.name) ?? 0) + 1;
      seen.set(t.name, n);
      if (n > 1 && !t.named) {
        t.name = `${t.name} ${n}`;
      }
    }
    return targets;
  }

  // The named target within snapping distance of a point, or null.
  function nearestNamedTarget(t) {
    if (!state.targetsOn) {
      return null;
    }
    let best = null;
    let bestD = TARGET_SNAP_RADIUS;
    for (const nt of state.targets) {
      const d = Math.hypot(nt.pos[0] - t[0], nt.pos[1] - t[1]);
      if (d < bestD) {
        bestD = d;
        best = nt;
      }
    }
    return best;
  }

  function setTarget(t, note) {
    // Snap to a named spot when the click is close to one: that is the whole
    // point of naming them. Two people who both mean "B doors" then get the
    // same coordinate, the cache serves one answer instead of two, and a vote
    // has one thing to attach to. Far from any named spot, the click is kept
    // exactly - it may be a spot nobody has named yet.
    const snap = nearestNamedTarget(t);
    if (snap) {
      t = [...snap.pos];
      state.targetName = snap.name;
      note = `${snap.named ? "" : "provisional: "}${snap.name}`;
    } else {
      state.targetName = null;
    }
    state.target = t;
    resetSearch({ keepTarget: true });
    syncUrl();
    // A 2D click names a column, not a point: where a map stacks floors (nuke's
    // A site over B, vertigo's levels) the solver resolves to the LOWEST
    // walkable height, which is rarely the one being pointed at. Ask instead.
    if (t.length < 3) {
      offerLevelChoice(t);
    }
    // Lead with the action most users want next (the full sweep); the
    // narrower solve-one-spot click is the refinement, not the default.
    statusEl.textContent = `${note} - now set a throw position, or "Search the whole map" to find every spot that can`;
    syncControls();
    renderLineups();
    // The coverage overlay is about THIS target, so a new one invalidates it.
    loadCoverage();
    draw();
    sync3d();
    // A 2-length target still has a floor to choose; the held spawn is spent
    // once that answer comes back through this same function.
    if (t.length >= 3) {
      usePendingOrigin();
    }
  }

  // The level chooser: one chip per stacked floor under the clicked column,
  // highest first (the way the map is read from above). Picking one pins the
  // target's Z so every later solve, arc and preview uses that floor.
  async function offerLevelChoice(t) {
    const backdrop = document.getElementById("level-backdrop");
    const holder = document.getElementById("level-choice");
    backdrop.hidden = true;
    holder.innerHTML = "";
    const levels = await fetchLevels(state.currentMap, t[0], t[1]);
    // Still the same click? A second target may have landed while this was in
    // flight, and the chooser belongs to the newest one.
    if (state.target !== t || levels.length < 2) {
      state.awaitingLevel = false;
      // One floor is not a question, but it is still an answer: a click from
      // above carries no height, and without this the target kept none - the
      // box then showed "setpos x y 0", which teleports whoever pastes it to
      // the bottom of the world.
      if (state.target === t && levels.length === 1) {
        setTarget([t[0], t[1], levels[0].z], `target ${t[0].toFixed(0)}, ${t[1].toFixed(0)}`);
        return;
      }
      syncControls();
      usePendingOrigin();
      return;
    }
    // Nothing else can proceed until this is answered: solving the wrong floor
    // costs a full sweep and answers a question nobody asked.
    state.awaitingLevel = true;
    syncControls();

    const title = document.createElement("div");
    title.className = "level-title";
    title.id = "level-title";
    title.textContent = `${levels.length} floors are stacked here`;
    const sub = document.createElement("div");
    sub.className = "level-sub";
    // Says which way to lean, because most of the time there is one: a click
    // lands on what you can see from above, and what you can see from above is
    // the highest floor in the column.
    sub.textContent = "A click from above cannot say which one you meant. "
      + "If you did not mean something underneath, pick the highest.";
    const list = document.createElement("div");
    list.className = "level-list";

    // Two floors can sit under one callout (nuke's vents and ramp both answer
    // to Hut), so a repeated name keeps its height to stay distinguishable.
    const nameCounts = {};
    for (const { name } of levels) {
      nameCounts[name] = (nameCounts[name] ?? 0) + 1;
    }
    // Highest first, the way a map is read from above.
    for (const { z, name } of [...levels].reverse()) {
      const b = document.createElement("button");
      b.type = "button";
      b.className = "btn";
      b.textContent = !name ? `height ${z.toFixed(0)}`
        : nameCounts[name] > 1 ? `${name} (height ${z.toFixed(0)})`
        : name;
      b.addEventListener("click", () => {
        backdrop.hidden = true;
        state.awaitingLevel = false;
        // +1 keeps the target just clear of the floor plane it names, the same
        // way a 3D pick lands on the surface rather than inside it.
        setTarget([t[0], t[1], z + 1], `target ${name || ""} ${t[0].toFixed(0)}, ${t[1].toFixed(0)}, ${z.toFixed(0)}`.trim());
      });
      list.append(b);
    }

    const cancel = document.createElement("button");
    cancel.type = "button";
    cancel.className = "level-cancel";
    cancel.textContent = "cancel and pick a different spot";
    cancel.addEventListener("click", () => {
      backdrop.hidden = true;
      state.awaitingLevel = false;
      resetSearch();
      syncUrl();
      statusEl.textContent = "target cleared - pick a spot";
      syncControls();
      renderLineups();
      draw();
    });

    holder.append(title, sub, list, cancel);
    backdrop.hidden = false;
    list.querySelector(".btn")?.focus();
    statusEl.textContent = `that spot has ${levels.length} floors stacked - pick which one you meant`;
  }

  // A position as the game says it, so what the box shows is what the console
  // takes and what the console prints is what the box accepts.
  //
  // Two decimals, not whole units: rounding to integers moved a pasted
  // position by up to half a unit per axis, and this tool answers questions at
  // 1u. Trailing zeros are trimmed so a position that really is round still
  // reads as one.
  //
  // A 2D click carries no height, so each box names the height that belongs to
  // its own half: the resolved floor for the target, the throw spot's own feet
  // for the origin. Naming the target's floor for a throw spot on another level
  // would hand out a setpos that puts the player through it.
  function setposOf(p, fallbackZ) {
    const z = (p[2] ?? fallbackZ ?? 0) + FEET_ABOVE_FLOOR;
    return `setpos ${coord(p[0])} ${coord(p[1])} ${coord(z)}`;
  }
  // `getpos` prints "setpos x y z;setang p y r" and its position is the EYE,
  // not the feet - the long-standing Source quirk. A bare "setpos x y z" is
  // what `setpos` itself takes, which is the feet, and is also what this tool
  // copies out.
  //
  // So the `setang` half is the tell for which of the two a paste is, and
  // without it a position copied out of this tool and pasted back in dropped
  // another eye height every time it made the round trip.
  function parseSetpos(text) {
    const m = text.match(/(-?[\d.]+)\s+(-?[\d.]+)\s+(-?[\d.]+)/);
    if (!m) {
      return null;
    }
    const feetZ = Number.parseFloat(m[3]) - (/setang/i.test(text) ? DEFAULT_EYE_HEIGHT : 0);
    return [Number.parseFloat(m[1]), Number.parseFloat(m[2]), feetZ];
  }

  function armPick(which) {
    state.picking = which === "target";
    state.pickingOrigin = which === "origin";
    canvas.classList.toggle("picking", state.picking || state.pickingOrigin);
    statusEl.textContent = which === "target"
      ? "click the map to place your smoke target (Esc cancels)"
      : "click the map to place the spot you throw from (Esc cancels)";
    syncControls();
  }

  pickBtn.addEventListener("click", () => {
    const typed = parseSetpos(targetIn.value);
    if (typed && (!state.target || setposOf(state.target, state.result?.target?.[2]) !== targetIn.value.trim())) {
      setTarget(typed, `target ${typed[0].toFixed(0)}, ${typed[1].toFixed(0)}`);
      return;
    }
    if (state.picking) {
      state.picking = false;
      canvas.classList.remove("picking");
      statusEl.textContent = "";
      syncControls();
      return;
    }
    armPick("target");
  });

  pickOriginBtn.addEventListener("click", () => {
    const typed = parseSetpos(originIn.value);
    const current = state.pendingOrigin ?? state.lastOrigin;
    if (typed && (!current || setposOf(current, state.result?.lineups?.[0]?.feet?.[2]) !== originIn.value.trim())) {
      useThrowSpot(typed);
      return;
    }
    if (state.pickingOrigin) {
      state.pickingOrigin = false;
      canvas.classList.remove("picking");
      statusEl.textContent = "";
      syncControls();
      return;
    }
    armPick("origin");
  });

  for (const [input, button] of [[targetIn, pickBtn], [originIn, pickOriginBtn]]) {
    input.addEventListener("keydown", e => {
      if (e.key === "Enter") {
        button.click();
      }
    });
  }

  // A throw spot with a target already set is a solve from that spot; without
  // one it waits, the same way a clicked spawn does.
  async function useThrowSpot(origin) {
    state.pickingOrigin = false;
    canvas.classList.remove("picking");
    // A 2D click names a column, not a point. Resolve its floor before the
    // spot goes anywhere near a setpos box, for the same reason the target
    // does: the height it lacks is the one that decides where you land.
    if (origin.length < 3) {
      const levels = await fetchLevels(state.currentMap, origin[0], origin[1]);
      if (levels.length) {
        origin = [origin[0], origin[1], levels[levels.length - 1].z];
      }
    }
    if (state.target) {
      runQuery({ target: state.target, origin });
    } else {
      state.pendingOrigin = origin;
      statusEl.textContent = "throwing from there - now set the target you want to smoke";
      syncControls();
      draw();
      sync3d();
    }
  }
  // Copy the target as a setpos command: pasteable back into the getpos box or
  // the game console. A 2D-picked target carries no Z; fall back to the searched
  // target's Z (the solve resolves it) so the copied command still round-trips.
  copyOriginBtn.addEventListener("click", () => {
    const o = state.lastOrigin;
    if (!o) { return; }
    // Ground height comes from the solve when it resolved one; a raw 2D click
    // carries none, and setpos drops the player onto the floor anyway.
    const z = o[2] ?? state.result?.lineups?.[0]?.feet?.[2] ?? state.result?.target?.[2] ?? 0;
    const cmd = `setpos ${Math.round(o[0])} ${Math.round(o[1])} ${Math.round(z)}`;
    navigator.clipboard.writeText(cmd).then(() => {
      copyOriginBtn.classList.add("copied");
      statusEl.textContent = `copied throw spot: ${cmd}`;
      setTimeout(() => copyOriginBtn.classList.remove("copied"), 1200);
    }).catch(() => { statusEl.textContent = `throw spot: ${cmd}`; });
  });

  copyTargetBtn.addEventListener("click", () => {
    if (!state.target) { return; }
    const [x, y] = state.target;
    const z = state.target[2] ?? state.result?.target?.[2] ?? 0;
    const cmd = `setpos ${Math.round(x)} ${Math.round(y)} ${Math.round(z)}`;
    navigator.clipboard.writeText(cmd).then(() => {
      copyTargetBtn.classList.add("copied");
      copyTargetBtn.title = `copied: ${cmd}`;
      statusEl.textContent = `copied target position: ${cmd}`;
      setTimeout(() => {
        copyTargetBtn.classList.remove("copied");
        copyTargetBtn.title = "Copy this position as a setpos command";
      }, 1200);
    }, () => {
      statusEl.textContent = "copy failed - clipboard blocked (is the tab focused?)";
    });
  });
  document.addEventListener("keydown", e => {
    if (e.key === "Escape" && (state.picking || state.pickingOrigin)) {
      state.picking = false;
      state.pickingOrigin = false;
      canvas.classList.remove("picking");
      statusEl.textContent = "";
      syncControls();
    }
  });
  for (const b of searchSeg.children) {
    b.addEventListener("click", () => {
      if (!state.target) {
        return;
      }
      const where = b.dataset.search;
      if (where === "spot" || where === "exact") {
        const origin = state.pendingOrigin ?? state.lastOrigin;
        if (origin) {
          runQuery(where === "exact"
            ? { target: state.target, origin, scope: "exact" }
            : { target: state.target, origin });
        }
        return;
      }
      runQuery(where === "spawns"
        ? { target: state.target, scope: "spawns" }
        : { target: state.target });
    });
  }
  resetViewBtn.addEventListener("click", resetView);
  spawnsBtn.addEventListener("click", () => {
    state.spawnsOn = !state.spawnsOn;
    press(spawnsBtn, state.spawnsOn);
    draw();
    sync3d();
  });
  targetsBtn.addEventListener("click", () => {
    state.targetsOn = !state.targetsOn;
    press(targetsBtn, state.targetsOn);
    try { localStorage.setItem("smokesolver.targetsOn", state.targetsOn ? "1" : "0"); } catch { /* not persisting is fine */ }
    draw();
    sync3d();
  });
  try {
    state.targetsOn = localStorage.getItem("smokesolver.targetsOn") !== "0";
  } catch {
    // Default stands.
  }
  // Collapse the results panel out of the way to see the map beneath it. A fresh
  // solve re-expands it (panel.js) since the point of searching is to see them.
  panelCollapseBtn.addEventListener("click", () => {
    const collapsed = panelEl.classList.toggle("collapsed");
    panelCollapseBtn.setAttribute("aria-expanded", collapsed ? "false" : "true");
    panelCollapseBtn.title = collapsed ? "Show results" : "Hide results";
    panelCollapseBtn.setAttribute("aria-label", collapsed ? "Show results" : "Hide results");
  });
  proSmokesBtn.addEventListener("click", () => {
    state.prosmokesOn = !state.prosmokesOn;
    syncControls();
    draw();
  });
  proSideSeg.addEventListener("click", (e) => {
    const btn = e.target.closest(".seg-btn");
    if (!btn) {
      return;
    }
    state.proSide = btn.dataset.side;
    syncControls();
    draw();
  });
  clearBtn.addEventListener("click", () => {
    resetSearch();
    syncUrl();
    statusEl.textContent = "";
    syncControls();
    renderLineups();
    draw();
  });
  heatBtn.addEventListener("click", () => {
    // Cycle: off -> coverage -> stand spots -> off.
    if (!state.heatOn) {
      state.heatSpots = false;
      setHeat(true);
    } else if (!state.heatSpots) {
      state.heatSpots = true;
      syncControls();
    } else {
      state.heatSpots = false;
      setHeat(false);
    }
    statusEl.textContent = !state.heatOn ? resultStatusText(filtered().length)
      : state.heatSpots
        ? "stand spots: bright = corner-pinned with a verified lineup, mid = wall-pinned, faint = open ground - see legend"
        : "heatmap: where a throw reaches, and where nothing does - see legend · click again for stand-spot quality";
    draw();
  });
  for (const el of ["a-tolerance", "a-reach", "a-stability", "a-scan"].map(id => document.getElementById(id))) {
    el.addEventListener("change", () => {
      syncControls();
      advancedCostNote();
      const factor = advancedCostFactor();
      statusEl.textContent = "advanced settings apply to your next Search / spot click" +
        (factor >= 2 ? ` - roughly ${Math.round(factor)}x slower` : "");
    });
  }
  for (const f of Object.values(state.filters)) {
    f.addEventListener("change", () => {
      state.selected = -1;
      syncControls();
      renderLineups();
      draw();
      sync3d();
    });
  }

  const previewModal = document.getElementById("preview-modal");
  const previewImg = document.getElementById("preview-img");
  document.getElementById("preview-close").addEventListener("click", () => closeModal(previewModal));
  previewModal.addEventListener("click", e => { if (e.target === previewModal) { closeModal(previewModal); } });

  // capturePreview() borrows the single shared camera/canvas, so two
  // in-flight captures would stomp each other's saved camera state -
  // serialize them behind one chain rather than letting rapid clicks
  // through the lineup list race.
  let previewChain = Promise.resolve();
  function queuePreview(fn) {
    const p = previewChain.then(fn, fn);
    previewChain = p.catch(() => {});
    return p;
  }

  // Rendering a preview pulls in the textured GLB. Even the smaller mobile tier
  // (~120-200MB decoded) is worth loading only on a deliberate tap on a phone,
  // never automatically - a shared-lineup link auto-selects its one lineup on
  // load, so an automatic heavy load there could still stutter or, on the
  // weakest devices, reload. So on touch / low-memory devices the preview is an
  // explicit tap, never automatic, and the heavy load only follows a deliberate
  // action (never a page load). Same device test that picks the mobile GLB tier.
  const heavyPreviewRisk = lowMemoryDevice;

  function previewTapButton(l, thumbEl, label) {
    thumbEl.classList.remove("loading");
    thumbEl.innerHTML = `<button type="button" class="preview-load">${label}</button>`;
    thumbEl.querySelector("button").onclick = () => {
      l._previewRequested = true;
      loadPreviewThumb(l, thumbEl);
    };
  }

  // Renders entirely client-side (capturePreview reuses the shared
  // camera/canvas already in this page), so no server round-trip.
  // Cached on the lineup itself so reselecting it (or the same result set
  // surviving a re-render) never re-renders a frame that already exists.
  async function loadPreviewThumb(l, thumbEl) {
    if (l._previewUrl) {
      thumbEl.innerHTML = `<img src="${l._previewUrl}" alt="first-person preview of this lineup">`;
      thumbEl.onclick = () => enlargePreview(l);
      return;
    }
    if (heavyPreviewRisk && !l._previewRequested) {
      previewTapButton(l, thumbEl, "Tap to load preview");
      return;
    }
    thumbEl.textContent = "rendering preview…";
    thumbEl.classList.add("loading");
    try {
      const url = await queuePreview(() => capturePreview({ feet: l.feet, type: l.type, pitchDeg: l.pitch, yawDeg: l.yaw }));
      l._previewUrl = url;
      thumbEl.classList.remove("loading");
      thumbEl.innerHTML = `<img src="${url}" alt="first-person preview of this lineup">`;
      thumbEl.onclick = () => enlargePreview(l);
    } catch (err) {
      resetEnsureTexturedScene();
      l._previewRequested = false;
      if (heavyPreviewRisk) {
        previewTapButton(l, thumbEl, "Preview failed - tap to retry");
      } else {
        thumbEl.classList.remove("loading");
        thumbEl.textContent = `preview failed: ${err.message}`;
      }
    }
  }

  function enlargePreview(l) {
    if (!l._previewUrl) {
      return;
    }
    previewImg.src = l._previewUrl;
    openModal(previewModal, () => closeModal(previewModal));
  }

  function toggleFavorite(l) {
    // Persisted per map, keyed by the throw itself, so a saved lineup survives
    // a re-solve, a filter change and a reload.
    setFavorite(state.currentMap, l, !l._favorite);
    renderLineups();
    syncControls();
  }

  // ---- the Saved view: your lineups, across maps ----

  function setPanelMode(mode) {
    state.panelMode = mode;
    // Leaving the saved list without any results underneath would leave a
    // panel with nothing in it; collapse it instead.
    renderLineups();
  }

  panelModeEl.addEventListener("click", e => {
    const btn = e.target.closest("[data-mode]");
    if (btn) {
      setPanelMode(btn.dataset.mode);
    }
  });

  openSavedBtn.addEventListener("click", () => {
    setPanelMode(state.panelMode === "saved" ? "results" : "saved");
  });

  // Reopen a saved lineup: switch maps if it lives on another one, put its
  // target back, then load it through the same path a shared link uses.
  async function openSaved(spec) {
    if (state.busy) {
      return;
    }
    if (spec.map !== state.currentMap) {
      mapSelect.value = spec.map;
      if (!(await loadMap(spec.map))) {
        return;
      }
    }
    if (!spec.target) {
      statusEl.textContent = "this saved lineup has no target recorded - it was saved by an older version";
      return;
    }
    state.panelMode = "results";
    setTarget([...spec.target]);
    await showSingleLineup(spec);
  }

  function forgetSaved(spec) {
    state.saved = state.saved.filter(s => !(s.map === spec.map && s.id === spec.id));
    if (spec.map === state.currentMap) {
      state.favorites.delete(spec.id);
      for (const l of state.result?.lineups ?? []) { l._favorite = isFavorite(l); }
    }
    persistSavedLocal();
    favoriteHooks.onChange?.();
    renderLineups();
    syncControls();
  }

  function removeLineup(l) {
    l._removed = true;
    if (state.selected === l._idx) {
      state.selected = -1;
    }
    renderLineups();
    draw();
    sync3d();
  }

  // Shared by the "3D" button and "Go to" (fly into a lineup's throw spot):
  // both need the mesh loaded and the loop running first. Returns the live
  // bundle on success, null if the load failed or was cancelled mid-flight.
  async function openView3d() {
    stage3d.style.display = "block";
    canvas.style.display = "none";
    syncControls();
    statusEl.textContent = "loading 3D mesh…";
    try {
      const t3 = await ensure3d();
      // Stage hidden = toggled off or map switched mid-load; t3 undefined =
      // init3d bailed because the map changed. Either way, abandon quietly.
      if (!t3 || stage3d.style.display === "none") {
        statusEl.textContent = "";
        return null;
      }
      t3.start();
      sync3d();
      syncProgress3d();
      syncMeshDiff3d();
      // A 3D view opened after a world state was chosen must start in that state.
      t3.setWorldState(brokenParam());
      t3.focusStage();
      return t3;
    } catch {
      resetEnsure3d();
      stage3d.style.display = "none";
      canvas.style.display = "block";
      syncControls();
      statusEl.textContent = "3D unavailable - this browser could not start WebGL, or the map mesh failed to load";
      draw();
      return null;
    }
  }

  // The precision the accuracy ring is judged against: the precision filter
  // when set, otherwise whatever landing tolerance the solve itself used.
  function slackWithin() {
    return Number.parseFloat(state.filters.precision.value) ||
      Number.parseFloat(document.getElementById("a-tolerance").value) || 80;
  }

  async function goToLineup(l) {
    const t3 = await openView3d();
    if (!t3) {
      return;
    }
    if (!previewModal.hidden) {
      closeModal(previewModal);
    }
    t3.flyTo({ feet: l.feet, type: l.type, pitchDeg: l.pitch, yawDeg: l.yaw });
    // Naming the stance matters for anyone lining this up against the game:
    // the camera sits at the eye height this throw is aimed from, and standing
    // in game to compare a crouched lineup puts the view 18u higher, which
    // moves every reference on screen.
    const stance = EYE_HEIGHT_BY_TYPE[l.type] ? "crouched" : "standing";
    statusEl.textContent =
      `dropped into this lineup's throw spot, at ${stance} eye height - drag to look, WASD to move`;
    // The accuracy ring: how far the feet can drift before this exact aim
    // stops landing within the precision in play. Fetched lazily and cached
    // on the lineup; a failure only costs the ring, never the Go to itself.
    try {
      const within = slackWithin();
      if (l._slack?.within !== within) {
        l._slack = await fetchSlack(state.currentMap, l, state.target, within, brokenParam());
      }
      draw();
      sync3d();
      const rs = l._slack.dirs.map(d => d[1]);
      const rmax = Math.max(...rs);
      statusEl.textContent = rmax < 1
        ? `stand EXACTLY here - even 1u of drift stops this aim landing within ${within}u`
        : `the ring shows how far you can stand from this spot and still land within ${within}u ` +
          `(${Math.min(...rs).toFixed(0)}-${rmax.toFixed(0)}u of slack) - drag to look, WASD to move`;
    } catch {
      // Ring unavailable; the camera drop already succeeded.
    }
  }

  initPanel({
    onSetTarget: setTarget, onSelect: select, onPreview: loadPreviewThumb,
    onGoTo: goToLineup, onFavorite: toggleFavorite, onRemove: removeLineup,
    onShare: shareLineup, onVote: voteOn,
    onOpenSaved: openSaved, onForgetSaved: forgetSaved,
  });
  initMap2d({ onSetTarget: setTarget, onSelect: select, onRunQuery: runQuery, onPickOrigin: pickOrigin, onPickThrowSpot: useThrowSpot });
  set3dCallbacks({ onSetTarget: setTarget, onSelect: select, onRunQuery: runQuery, onPickOrigin: pickOrigin, onPickThrowSpot: useThrowSpot });

  // Derived, not constant: "click terrain" means set-target only until a
  // target exists, then it means solve-from-here - a static string was
  // telling users the wrong thing for most of the session. Space/Ctrl leads
  // because that is CS2's own spectator freecam pair; Q/E stay as aliases.
  // Touch has no WASD/scroll/right-click, so a phone gets the short gesture
  // hint (which otherwise wraps to three wasted lines above the map).
  const coarsePointer = matchMedia("(pointer: coarse)").matches;
  const hint3d = () => coarsePointer
    ? "3D: 1 finger look · 2 fingers pan/zoom · " + (state.target ? "tap terrain = solve there · long-press = move target" : "tap terrain = set target")
    : "3D: WASD fly (Space/Ctrl up/down, Shift fast) · drag look · right-drag pan · scroll dolly · " +
      (state.target ? "click terrain = solve from that spot · right-click = move target" : "click terrain = set target");

  // A latch, not a jump: the row it sits in is a set of things that are either
  // on or off, so leaving it lets the camera go back where it was rather than
  // stranding the view overhead.
  let cameraBeforeTopDown = null;
  topDownBtn.addEventListener("click", () => {
    const t3 = current3d();
    if (!t3) {
      return;
    }
    if (state.topDownOn) {
      state.topDownOn = false;
      if (cameraBeforeTopDown) {
        t3.camera.position.fromArray(cameraBeforeTopDown.pos);
        t3.camera.quaternion.fromArray(cameraBeforeTopDown.quat);
        t3.requestRender();
      }
      statusEl.textContent = hint3d();
    } else {
      cameraBeforeTopDown = { pos: t3.camera.position.toArray(), quat: t3.camera.quaternion.toArray() };
      state.topDownOn = true;
      t3.topDown();
      statusEl.textContent = "looking straight down at the map";
    }
    syncControls();
    // Otherwise focus stays on the button, and the fly keys - which ignore
    // anything typed at a button - stay dead until the view is clicked.
    t3.focusStage();
  });

  // One handler for the whole View row: leaving 3D, entering it, and switching
  // the map's skin are the same decision, so they are the same control.
  async function selectView(mode) {
    const in3d = stage3d.style.display !== "none";
    if (mode === "2d") {
      if (in3d) {
        current3d()?.stop();
        stage3d.style.display = "none";
        canvas.style.display = "block";
        state.topDownOn = false;
        draw();
        // The 3D "WASD fly…" hint is meaningless in 2D; restore the 2D status
        // (result summary if a solve is up, otherwise clear it).
        statusEl.textContent = state.result && !state.heatOn ? resultStatusText(filtered().length) : "";
      }
      syncControls();
      return;
    }
    const t3 = in3d ? current3d() : await openView3d();
    if (!t3) {
      return;
    }
    const wantTextures = mode === "textured";
    if (t3.isTextured !== wantTextures) {
      for (const b of viewSeg.children) { b.disabled = true; }
      statusEl.textContent = wantTextures
        ? "loading real map textures (one-time, size varies by map)…" : "";
      try {
        await t3.setTextured(wantTextures);
      } catch (err) {
        resetEnsureTexturedScene();
        statusEl.textContent = `failed to load textures: ${err.message}`;
        syncControls();
        return;
      } finally {
        for (const b of viewSeg.children) { b.disabled = false; }
      }
    }
    statusEl.textContent = hint3d();
    if (state.reticleOn) { buildRuler(rulerEl); }
    syncControls();
    t3.focusStage();
  }
  for (const b of viewSeg.children) {
    b.addEventListener("click", () => selectView(b.dataset.view));
  }

  matchMedia("(prefers-color-scheme: dark)").addEventListener("change", () => {
    readColors();
    recolorRadar();
    draw();
    applyTheme3d();
  });

  // A resolution media query fires once per boundary crossing, so re-register
  // after each change to keep tracking DPR across monitor moves (M45).
  (function watchDpr() {
    matchMedia(`(resolution: ${window.devicePixelRatio}dppx)`).addEventListener(
      "change", () => { resize(); current3d()?.resize3d(); watchDpr(); }, { once: true });
  })();

  // A plain window resize (desktop drag, phone rotation) changes the canvas CSS
  // size but not its backing store, so the 2D map drew stale/blank until the
  // next interaction. Re-fit the backing store to the new size on resize; the
  // 3D view keeps its own resize handler. Coalesced to one redraw per frame.
  let resizeQueued = false;
  window.addEventListener("resize", () => {
    if (resizeQueued) { return; }
    resizeQueued = true;
    requestAnimationFrame(() => {
      resizeQueued = false;
      resize();
      // The ruler's tick positions are angles projected onto this viewport, so
      // a reshaped window moves the horizontal ones.
      if (state.reticleOn) { buildRuler(rulerEl); }
    });
  });

  // Below the breakpoint the actions card collapses to a <details>; CSS cannot
  // force a closed details open again at desktop width, so sync it here. Filters
  // are excluded on purpose - that one is the user's to open and close.
  const compactMq = matchMedia("(max-width: 640px)");
  const syncCompactControls = () => {
    document.getElementById("card-view").open = !compactMq.matches;
  };
  syncCompactControls();
  compactMq.addEventListener("change", syncCompactControls);
})();

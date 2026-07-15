// Boot and cross-module orchestration. This is the only module allowed to
// import the feature modules; they call back into the orchestrators defined
// here (setTarget, select, runQuery) via the init*/set*Callbacks hooks.

import { state, filtered, esc } from "./state.js";
import { loadMapList, loadMapData, runQuery as postLineupQuery, fetchTrajectory } from "./api.js";
import { loadRadar, readColors, recolorRadar, draw, scheduleDraw, resize, resetView, initMap2d } from "./map2d.js";
import { ensure3d, resetEnsure3d, resetEnsureTexturedScene, teardown3d, current3d, sync3d, syncProgress3d, set3dCallbacks, applyTheme3d, capturePreview } from "./view3d.js";
import { renderLineups, initPanel, revealSelected } from "./panel.js";

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

  const canvas = state.canvas;
  const stage3d = state.stage3d;
  const statusEl = state.statusEl;
  const pickBtn = document.getElementById("pick");
  const searchBtn = document.getElementById("search-all");
  const heatBtn = document.getElementById("heat");
  const view3dBtn = document.getElementById("view3d");
  const texturedBtn = document.getElementById("textured3d");
  const topDownBtn = document.getElementById("topdown");
  const clearBtn = document.getElementById("clear");
  const keyEl = document.getElementById("key-dots");
  const mapSelect = document.getElementById("map-select");
  const cancelBtn = document.getElementById("solve-cancel");
  let solveController = null;
  cancelBtn.addEventListener("click", () => solveController?.abort());

  // Fetched once per lineup and cached on it: a throw's arc is fixed for a given
  // map build, and only the selected one is ever drawn. A failure here is not
  // worth interrupting anyone over - the straight throw-spot-to-landing line is
  // still drawn, it just does not curve.
  async function loadPath(l) {
    if (l._path || l._pathFailed) {
      return;
    }
    try {
      l._path = (await fetchTrajectory(state.currentMap, l)).points;
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
    renderLineups();
    if (state.selected >= 0) {
      revealSelected();
      loadPath(state.result.lineups[state.selected]);
    }
    draw();
    sync3d();
  }

  // Every control's state is a pure function of what the user has actually done,
  // so it is derived in one place rather than patched from each handler - which
  // is how "Target" ended up looking permanently pressed. A control that cannot
  // do anything yet is absent, not greyed out, so the card reads as a sequence.
  function syncControls() {
    const hasTarget = !!state.target;
    const in3d = stage3d.style.display !== "none";

    // Exactly one control is ever the filled primary: whatever the next step in
    // the sequence is. Pick a target, then search from it, then the tool has
    // nothing to urge and everything goes quiet.
    pickBtn.textContent = state.picking ? "Click the map…" : hasTarget ? "Re-target" : "Target";
    pickBtn.classList.toggle("armed", state.picking);
    pickBtn.classList.toggle("primary", !state.picking && !hasTarget);

    searchBtn.hidden = !hasTarget;
    searchBtn.disabled = state.busy;
    searchBtn.classList.toggle("primary", hasTarget && !state.result && !state.busy);
    clearBtn.hidden = !hasTarget;

    heatBtn.hidden = !state.result?.coverage;
    heatBtn.classList.toggle("active", state.heatOn);

    view3dBtn.classList.toggle("active", in3d);
    texturedBtn.hidden = !in3d;
    topDownBtn.hidden = !in3d;
    document.body.classList.toggle("crosshair-3d", in3d);

    // Nothing to explain until there are markers on the map to explain.
    keyEl.hidden = !hasTarget;
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
    // the result count would look wrong with no visible cause.
    const active = Object.values(state.filters).filter(f => f.value).length;
    document.getElementById("filter-count").textContent = active ? `(${active})` : "";
  }

  // Heat mode swaps the map key from marker shapes to coverage colors (L23).
  function setHeat(on) {
    state.heatOn = on;
    syncControls();
  }

  // A different map is entirely different geometry/nav/lineups, so this is a
  // full reset (target, results, 3D scene) rather than an incremental swap -
  // there is only ever "the current map," never a per-map cache to switch
  // back into. Returns false (and shows the boot error) if the map's data
  // failed to load, so the caller can bail out the same way initial boot did.
  async function loadMap(name) {
    solveController?.abort();
    teardown3d();
    stage3d.style.display = "none";
    canvas.style.display = "block";
    texturedBtn.classList.remove("active");
    state.currentMap = name;
    localStorage.setItem("smokesolver.lastMap", name);
    state.picking = false;
    state.target = null;
    state.result = null;
    state.selected = -1;
    state.heatOn = false;
    canvas.classList.remove("picking");
    syncControls();

    try {
      state.mapData = await loadMapData(name);
    } catch {
      bootError(`data/${name}.viewer-map.json`);
      return false;
    }
    try {
      await loadRadar();
    } catch {
      bootError("data/" + state.mapData.image);
      return false;
    }
    clearBootError();
    recolorRadar();
    resize();
    resetView();
    renderLineups();
    statusEl.textContent = "";
    return true;
  }

  let mapList;
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
  function mountIntroFilters() {
    document.getElementById("intro-filter-rows").innerHTML = filterEls
      .map(f => `<div class="filter-row" data-for="${f.id}">` +
        `<label class="filter-head" for="${f.id}"><b>${esc(f.dataset.label)}:</b><span class="filter-slot"></span></label>` +
        `<p class="filter-desc">${esc(f.dataset.desc)}</p></div>`)
      .join("");
    for (const f of filterEls) {
      document.querySelector(`.filter-row[data-for="${f.id}"] .filter-slot`).appendChild(f);
    }
  }

  function closeIntro() {
    for (const f of filterEls) {
      filterBody.appendChild(f);
    }
    intro.hidden = true;
    syncControls();
    statusEl.textContent = "click Target, then click the map";
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
    if (!(await loadMap(initialMap))) {
      return;
    }
  } else {
    mountIntroFilters();
    intro.hidden = false;
  }

  // Progress lines stream in every ~100ms; painting each batch as dots shows
  // both sweep speed and which standable origins were actually evaluated.
  function onSolveProgress(msg) {
    const p = state.progress;
    if (!p) {
      return;
    }
    if (msg.phase === "prepare") {
      statusEl.textContent = "preparing voxel grid…";
    } else if (msg.phase === "sweep") {
      p.phase = "sweep";
      p.total = msg.count;
    } else if (msg.phase === "verify") {
      p.phase = "verify";
      p.candidates = msg.count;
      statusEl.textContent = `verifying 0 / ${msg.count} candidates against the exact sim…`;
    } else if (msg.checked) {
      p.checked.push(...msg.checked);
      // Naming what is being counted matters: the total is every spot a player
      // can stand within throw range of the target, not the whole map, which is
      // why it lands near the same figure regardless of how big the map is.
      statusEl.textContent = `checked ${p.checked.length}${p.total ? ` / ${p.total}` : ""} stand spots in throw range…`;
    } else if (msg.verified) {
      p.verified.push(...msg.verified);
      statusEl.textContent = `verifying ${p.verified.length} / ${p.candidates ?? "?"} candidates against the exact sim…`;
    }
    scheduleDraw();
    syncProgress3d();
  }

  async function runQuery(body) {
    state.busy = true;
    state.progress = { phase: "sweep", total: 0, candidates: 0, checked: [], verified: [] };
    statusEl.textContent = "solving…";
    solveController = new AbortController();
    cancelBtn.hidden = false;
    try {
      const { error, data } = await postLineupQuery({ ...body, map: state.currentMap }, solveController.signal, onSolveProgress);
      if (error) {
        statusEl.textContent = error;
        return;
      }
      const next = data;
      if (next.lineups.length === 0) {
        // A single-origin probe checked one spot, not the map; saying "any of
        // N stand spots" for it would wrongly read as an exhaustive sweep.
        statusEl.textContent = body.origin
          ? `no throw from that spot reaches the target - "Search map" sweeps every spot that can`
          : `no throw reaches there from any of the ${next.origins} stand spots in range - try another target`;
        return;
      }
      next.lineups.forEach((l, i) => { l._idx = i; });
      state.result = next;
      // Adopt the server's resolved target, which carries the ground Z it snapped
      // a 2D (Z-less) pick onto. Keeps the 2D and 3D target at the same height the
      // sim actually used, so switching views no longer moves or floats it.
      if (Array.isArray(next.target) && next.target.length > 2) {
        state.target = next.target;
      }
      state.selected = -1;
      renderLineups();
      sync3d();
    } catch (err) {
      statusEl.textContent = err.name === "AbortError" ? "cancelled" : `error: ${err.message}`;
    } finally {
      state.busy = false;
      state.progress = null;
      solveController = null;
      cancelBtn.hidden = true;
      syncControls();
      draw();
      syncProgress3d();
    }
  }

  function setTarget(t, note) {
    state.target = t;
    state.picking = false;
    state.result = null;
    state.selected = -1;
    state.heatOn = false;
    canvas.classList.remove("picking");
    // Lead with the action most users want next (the full sweep); the
    // narrower solve-one-spot click is the refinement, not the default.
    statusEl.textContent = `${note} - "Search map" finds every throw spot · or click one spot to solve just it · right-click/long-press moves the target`;
    syncControls();
    renderLineups();
    draw();
    sync3d();
  }

  pickBtn.addEventListener("click", () => {
    state.picking = !state.picking;
    canvas.classList.toggle("picking", state.picking);
    statusEl.textContent = state.picking ? "click the map to place your smoke target (Esc cancels)" : "";
    syncControls();
  });
  document.addEventListener("keydown", e => {
    if (e.key === "Escape" && state.picking) {
      state.picking = false;
      canvas.classList.remove("picking");
      statusEl.textContent = "";
      syncControls();
    }
  });
  searchBtn.addEventListener("click", () => {
    if (state.target && !state.busy) {
      statusEl.textContent = "searching map…";
      runQuery({ target: state.target });
    }
  });
  clearBtn.addEventListener("click", () => {
    state.picking = false;
    state.target = null;
    state.result = null;
    state.selected = -1;
    state.heatOn = false;
    canvas.classList.remove("picking");
    statusEl.textContent = "";
    syncControls();
    renderLineups();
    draw();
  });
  heatBtn.addEventListener("click", () => {
    setHeat(!state.heatOn);
    statusEl.textContent = state.heatOn
      ? "heatmap: where a throw reaches, and where nothing does - see legend"
      : `${filtered().length} lineups - click a marker or use the list`;
    draw();
  });
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
  document.getElementById("preview-close").addEventListener("click", () => { previewModal.hidden = true; });
  previewModal.addEventListener("click", e => { if (e.target === previewModal) { previewModal.hidden = true; } });

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

  // Renders entirely client-side (capturePreview reuses the shared
  // camera/canvas already in this page), so no server round-trip - just a
  // one-time texture load (size varies by map, 26-92MB) the first time any
  // preview is requested.
  // Cached on the lineup itself so reselecting it (or the same result set
  // surviving a re-render) never re-renders a frame that already exists.
  async function loadPreviewThumb(l, thumbEl) {
    if (l._previewUrl) {
      thumbEl.innerHTML = `<img src="${l._previewUrl}" alt="first-person preview of this lineup">`;
      thumbEl.onclick = () => enlargePreview(l);
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
      thumbEl.classList.remove("loading");
      thumbEl.textContent = `preview failed: ${err.message}`;
    }
  }

  function enlargePreview(l) {
    if (!l._previewUrl) {
      return;
    }
    previewImg.src = l._previewUrl;
    previewModal.hidden = false;
  }

  function toggleFavorite(l) {
    l._favorite = !l._favorite;
    renderLineups();
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
      if (stage3d.style.display === "none") { // toggled off while loading
        statusEl.textContent = "";
        return null;
      }
      t3.start();
      sync3d();
      syncProgress3d();
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

  async function goToLineup(l) {
    const t3 = await openView3d();
    if (!t3) {
      return;
    }
    previewModal.hidden = true;
    t3.flyTo({ feet: l.feet, type: l.type, pitchDeg: l.pitch, yawDeg: l.yaw });
    statusEl.textContent = "dropped into this lineup's throw spot - drag to look, WASD to move";
  }

  initPanel({
    onSetTarget: setTarget, onSelect: select, onPreview: loadPreviewThumb,
    onGoTo: goToLineup, onFavorite: toggleFavorite, onRemove: removeLineup,
  });
  initMap2d({ onSetTarget: setTarget, onSelect: select, onRunQuery: runQuery });
  set3dCallbacks({ onSetTarget: setTarget, onSelect: select, onRunQuery: runQuery });

  // Derived, not constant: "click terrain" means set-target only until a
  // target exists, then it means solve-from-here - a static string was
  // telling users the wrong thing for most of the session. Space/Ctrl leads
  // because that is CS2's own spectator freecam pair; Q/E stay as aliases.
  const hint3d = () =>
    "3D: WASD fly (Space/Ctrl up/down, Shift fast) · drag look · right-drag pan · scroll dolly · " +
    (state.target ? "click terrain = solve from that spot · right-click = move target" : "click terrain = set target");

  topDownBtn.addEventListener("click", () => {
    const t3 = current3d();
    if (!t3) {
      return;
    }
    t3.topDown();
    // Otherwise focus stays on the button, and the fly keys - which ignore
    // anything typed at a button - stay dead until the view is clicked.
    t3.focusStage();
    statusEl.textContent = "looking straight down at the map";
  });

  view3dBtn.addEventListener("click", async () => {
    if (stage3d.style.display !== "none") {
      current3d()?.stop();
      stage3d.style.display = "none";
      canvas.style.display = "block";
      syncControls();
      draw();
      return;
    }
    const t3 = await openView3d();
    if (t3) {
      statusEl.textContent = hint3d();
    }
  });

  texturedBtn.addEventListener("click", async () => {
    const t3 = current3d();
    if (!t3) {
      return;
    }
    const wantOn = !t3.isTextured;
    texturedBtn.disabled = true;
    statusEl.textContent = wantOn ? "loading real map textures (one-time, size varies by map)…" : "";
    try {
      await t3.setTextured(wantOn);
      texturedBtn.classList.toggle("active", wantOn);
      statusEl.textContent = hint3d();
    } catch (err) {
      resetEnsureTexturedScene();
      statusEl.textContent = `failed to load textures: ${err.message}`;
    } finally {
      texturedBtn.disabled = false;
      t3.focusStage();
    }
  });

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
